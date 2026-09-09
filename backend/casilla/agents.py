"""Agents for the casilla INE simulation: voters, stations, and the
coordinator that relays external events between them.

Communication between agents is explicit: every handoff (a station giving
a voter a turn, telling it to wait, or the coordinator pausing/resuming a
station) goes through a ``Message`` object logged at send time, not a bare
method call.

"""

from __future__ import annotations

import functools
import logging
from collections import deque
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

from mesa import Agent

logger = logging.getLogger(__name__)

ADULTO_MAYOR_THRESHOLD = 60
CANDIDATOS = ["A", "B", "C"]
CANDIDATO_WEIGHTS = None  # None = equal probability for every candidate


@dataclass
class Message:
    """An explicit message passed between agents (or from the environment)."""

    sender: Any
    receiver: Any
    type: str
    payload: dict | None = field(default=None)
    time: float = 0.0


def _sender_label(sender: Any) -> str:
    if isinstance(sender, str):
        return sender
    name = getattr(sender, "name", None)
    if name is not None:
        return name
    return type(sender).__name__


class VoterAgent(Agent):
    """A voter moving through the casilla's stations."""

    def __init__(self, model, number: int) -> None:
        super().__init__(model)
        # 1-based display number; Mesa's own unique_id is shared across all
        # agents, so the first voter would otherwise log as "Votante 6".
        self.number = number
        self.edad = model.random.randint(18, 90)
        self.es_adulto_mayor = self.edad >= ADULTO_MAYOR_THRESHOLD
        self.voto = model.random.choices(CANDIDATOS, weights=CANDIDATO_WEIGHTS)[0]
        self.status: str = "arrived"
        self.timestamps: dict[str, float] = {}

    def receive_message(self, message: Message) -> None:
        logger.info(
            "Votante %s recibe %s de %s en t=%.2f",
            self.number,
            message.type,
            _sender_label(message.sender),
            message.time,
        )
        station = (message.payload or {}).get("station")
        if message.type == "TURN":
            self.status = f"en_{station}"
            self.timestamps[station] = message.time
        elif message.type == "WAIT":
            self.status = f"esperando_{station}"
        elif message.type == "REJECTED":
            self.status = "rechazado"


class Station(Agent):
    """A capacity-limited resource with FIFO wait queues.

    Mesa's own scheduler has no built-in limited-capacity resource, so this
    fills that gap: voters either start service immediately (if there's
    free capacity and the station isn't paused) or wait in one of two FIFO
    queues until it is their turn — a priority queue for adultos mayores
    (``edad >= ADULTO_MAYOR_THRESHOLD``), always drained before the regular
    queue.
    """

    def __init__(
        self,
        model,
        name: str,
        capacity: int = 1,
        service_time_range: tuple[float, float] = (1.0, 1.0),
    ) -> None:
        super().__init__(model)
        self.name = name
        self.capacity = capacity
        self.service_time_range = service_time_range
        self.busy = 0
        self.queue: deque[VoterAgent] = deque()
        self.priority_queue: deque[VoterAgent] = deque()
        self.paused = False
        self.on_complete: Callable[[VoterAgent], None] | None = None

    def request(self, voter: VoterAgent) -> None:
        if self.paused or self.busy >= self.capacity:
            queue_type = "priority" if voter.es_adulto_mayor else "regular"
            target = self.priority_queue if voter.es_adulto_mayor else self.queue
            position = len(target)
            target.append(voter)
            self.model.event_log.append(
                {
                    "event": "QUEUE_JOIN",
                    "voter": voter.number,
                    "station": self.name,
                    "queue_type": queue_type,
                    "position": position,
                    "time": self.model.time,
                }
            )
            self._send(voter, "WAIT")
            return
        self._start_service(voter)

    def _start_service(self, voter: VoterAgent) -> None:
        self.busy += 1
        self.model.event_log.append(
            {
                "event": f"{self.name.upper()}_START",
                "voter": voter.number,
                "station": self.name,
                "time": self.model.time,
            }
        )
        self._send(voter, "TURN")
        service_time = self.model.random.uniform(*self.service_time_range)
        self.model.schedule_callback(
            functools.partial(self._complete_service, voter),
            after=service_time,
        )

    def _complete_service(self, voter: VoterAgent) -> None:
        self.busy -= 1
        logger.info(
            "%s termina con votante %s en t=%.2f",
            self.name,
            voter.number,
            self.model.time,
        )
        self.model.event_log.append(
            {
                "event": f"{self.name.upper()}_DONE",
                "voter": voter.number,
                "station": self.name,
                "time": self.model.time,
            }
        )
        if self.on_complete is not None:
            self.on_complete(voter)
        self._pull_from_queue()

    def _pull_from_queue(self) -> None:
        while not self.paused and self.busy < self.capacity:
            if self.priority_queue:
                next_voter = self.priority_queue.popleft()
                queue_type = "priority"
            elif self.queue:
                next_voter = self.queue.popleft()
                queue_type = "regular"
            else:
                break
            self.model.event_log.append(
                {
                    "event": "QUEUE_LEAVE",
                    "voter": next_voter.number,
                    "station": self.name,
                    "queue_type": queue_type,
                    "time": self.model.time,
                }
            )
            self._start_service(next_voter)

    def receive_message(self, message: Message) -> None:
        logger.info(
            "%s recibe %s de %s en t=%.2f",
            self.name,
            message.type,
            _sender_label(message.sender),
            message.time,
        )
        if message.type == "PAUSE":
            self.paused = True
        elif message.type == "RESUME":
            self.paused = False
            self._pull_from_queue()

    def _send(self, receiver: VoterAgent, type_: str) -> None:
        message = Message(
            sender=self,
            receiver=receiver,
            type=type_,
            payload={"station": self.name},
            time=self.model.time,
        )
        logger.info(
            "%s envia %s a votante %s en t=%.2f",
            self.name,
            type_,
            receiver.number,
            self.model.time,
        )
        receiver.receive_message(message)


class Coordinador(Agent):
    """Relays external events to every station (pause/resume broadcast)."""

    def __init__(self, model, stations: list[Station]) -> None:
        super().__init__(model)
        self.stations = stations

    def receive_message(self, message: Message) -> None:
        if message.type != "EXTERNAL_EVENT":
            return
        payload = message.payload or {}
        kind = payload.get("kind", "evento")
        duration = payload.get("duration", 0.0)
        logger.info(
            "Coordinador recibe EXTERNAL_EVENT (%s, dura %.2f min) en t=%.2f",
            kind,
            duration,
            message.time,
        )
        self.broadcast("PAUSE")
        self.model.schedule_callback(
            functools.partial(self.broadcast, "RESUME"),
            after=duration,
        )

    def broadcast(self, type_: str, payload: dict | None = None) -> None:
        for station in self.stations:
            message = Message(
                sender=self,
                receiver=station,
                type=type_,
                payload=payload,
                time=self.model.time,
            )
            logger.info(
                "Coordinador envia %s a %s en t=%.2f",
                type_,
                station.name,
                self.model.time,
            )
            station.receive_message(message)

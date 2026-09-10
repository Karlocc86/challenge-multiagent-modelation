"""Event-driven core of the casilla INE simulation.

Orchestrates voter arrivals, chains them through the secretario -> mesa ->
casilla -> urna stations (each a capacity-limited FIFO resource), and
injects one external event that pauses every station for a while. All of
it rides on Mesa's built-in priority-queue event scheduler
(``Model.schedule_event`` / ``run_until``), never a fixed-tick loop.
"""

from __future__ import annotations

import functools
import logging
from collections.abc import Callable
from typing import Any

from mesa import Model
from mesa.time import Event, Priority

from .agents import Coordinador, Message, Station, VoterAgent
from .arrivals import (
    sample_beta_arrivals,
    validate_beta_mixture,
)

logger = logging.getLogger(__name__)


# Values are in simulated minutes.
STATION_SERVICE_TIMES: dict[str, tuple[float, float]] = {
    "secretario": (1.5, 2.5),
    "mesa": (0.5, 1.5),
    "casilla": (2.0, 4.0),
    "urna": (0.2, 0.6),
}

# Walking time for each leg of the journey (also simulated minutes). Unity
# owns real floor-plan coordinates, so the backend only needs a rough time
# per leg, not distances.
TRANSIT_TIMES: dict[tuple[str, str], tuple[float, float]] = {
    ("entrada", "secretario"): (0.3, 0.6),
    ("secretario", "mesa"): (0.1, 0.3),
    ("mesa", "casilla"): (0.2, 0.4),
    ("casilla", "urna"): (0.1, 0.2),
    ("urna", "salida"): (0.2, 0.5),
}

EXTERNAL_EVENT_KINDS = ["corte_de_luz", "temblor", "aguacero"]


class CasillaModel(Model):
    """Schedules voter arrivals and runs them through the station chain."""

    def __init__(
        self,
        num_voters: int = 200,
        arrival_rate: float = 1 / 3,
        *,
        arrival_beta: dict | None = None,
        secretario_capacity: int = 1,
        mesa_capacity: int = 1,
        casilla_capacity: int = 1,
        urna_capacity: int = 1,
        rejection_rate: float = 0.02,
        forced_event_kind: str | None = None,
        forced_event_time: float | None = None,
        forced_event_duration: float | None = None,
        rng: int | None = None,
    ) -> None:
        if isinstance(num_voters, bool) or not isinstance(num_voters, int) or num_voters < 0:
            raise ValueError("num_voters debe ser un entero mayor o igual que 0.")
        self.arrival_beta = None if arrival_beta is None else validate_beta_mixture(arrival_beta)
        super().__init__(rng=rng)

        # Mesa's Model starts a hidden recurring step() event by default;
        # stop it since this model never calls step() and Mesa 3.5.1 has no
        # public API to opt out at construction time.
        self._default_schedule.stop()

        self._scheduled_callbacks: list[Any] = []
        self._voter_counter = 0
        self.event_log: list[dict] = []
        self.last_scheduled_arrival_time: float | None = None
        self.rejection_rate = rejection_rate
        # None keeps the external event random. Forcing one lets a demo show a
        # specific kind on cue instead of hunting for a seed that produces it.
        self.forced_event_kind = forced_event_kind
        self.forced_event_time = forced_event_time
        self.forced_event_duration = forced_event_duration


        self.secretario = Station(
            self,
            "secretario",
            capacity=secretario_capacity,
            service_time_range=STATION_SERVICE_TIMES["secretario"],
        )
        self.mesa = Station(
            self,
            "mesa",
            capacity=mesa_capacity,
            service_time_range=STATION_SERVICE_TIMES["mesa"],
        )
        self.casilla = Station(
            self,
            "casilla",
            capacity=casilla_capacity,
            service_time_range=STATION_SERVICE_TIMES["casilla"],
        )
        self.urna = Station(
            self,
            "urna",
            capacity=urna_capacity,
            service_time_range=STATION_SERVICE_TIMES["urna"],
        )
        self.coordinador = Coordinador(
            self, stations=[self.secretario, self.mesa, self.casilla, self.urna]
        )

        self.secretario.on_complete = self._on_secretario_done  # branches on INE rejection
        self.mesa.on_complete = lambda voter: self._start_transit(
            voter, "mesa", "casilla", self.casilla.request
        )
        self.casilla.on_complete = lambda voter: self._start_transit(
            voter, "casilla", "urna", self.urna.request
        )
        self.urna.on_complete = self._on_exit

        self._schedule_arrivals(num_voters, arrival_rate)
        self._schedule_external_event()

    def schedule_callback(
        self,
        fn,
        *,
        at: float | None = None,
        after: float | None = None,
        priority: Priority = Priority.DEFAULT,
    ) -> Event:
        """``schedule_event``, but keeps a strong reference to ``fn`` alive
        since Mesa holds callbacks by weak reference and would otherwise GC
        an inline ``functools.partial`` before it fires.
        """
        self._scheduled_callbacks.append(fn)
        return self.schedule_event(fn, at=at, after=after, priority=priority)

    def run_to_completion(self) -> None:
        """Advance the clock event by event until the queue is empty."""
        while not self._event_list.is_empty():
            next_time = self._event_list.peek_ahead(1)[0].time
            self.run_until(next_time)

    def _schedule_arrivals(self, num_voters: int, arrival_rate: float) -> None:
        if self.arrival_beta is not None:
            for time in sample_beta_arrivals(self.random, num_voters, self.arrival_beta):
                self.schedule_callback(self._on_voter_arrival, at=time)
                self.last_scheduled_arrival_time = time
            return
        time = 0.0
        for _ in range(num_voters):
            time += self.random.expovariate(arrival_rate)
            self.schedule_callback(self._on_voter_arrival, at=time)
            self.last_scheduled_arrival_time = time

    def _on_voter_arrival(self) -> None:
        self._voter_counter += 1
        voter = VoterAgent(self, number=self._voter_counter)
        self.event_log.append(
            {
                "event": "ARRIVAL",
                "voter": voter.number,
                "time": self.time,
                "edad": voter.edad,
                "voto": voter.voto,
            }
        )
        logger.info("Votante %s llega en t=%.2f", voter.number, self.time)
        self._start_transit(voter, "entrada", "secretario", self.secretario.request)

    def _on_secretario_done(self, voter: VoterAgent) -> None:
        if self.random.random() < self.rejection_rate:
            message = Message(
                sender=self.secretario,
                receiver=voter,
                type="REJECTED",
                payload={"reason": "INE invalida"},
                time=self.time,
            )
            voter.receive_message(message)
            self.event_log.append(
                {"event": "REJECTED", "voter": voter.number, "time": self.time}
            )
            logger.info(
                "Votante %s RECHAZADO (INE invalida) en t=%.2f", voter.number, self.time
            )
            return
        self._start_transit(voter, "secretario", "mesa", self.mesa.request)

    def _on_exit(self, voter: VoterAgent) -> None:
        self.event_log.append(
            {"event": "EXIT", "voter": voter.number, "time": self.time}
        )
        logger.info("Votante %s EXITS en t=%.2f", voter.number, self.time)
        self._start_transit(voter, "urna", "salida", lambda v: None)

    def _start_transit(
        self,
        voter: VoterAgent,
        from_name: str,
        to_name: str,
        then: Callable[[VoterAgent], None],
    ) -> None:
        """Log a walking segment and call ``then(voter)`` once it ends.

        Stations hand voters off instantly in simulated time; this is the
        only place that inserts real walking time between them, so Unity
        has a (from, to, t_start, t_end) segment to Lerp instead of a
        teleport.
        """
        t0 = self.time
        transit = self.random.uniform(*TRANSIT_TIMES[(from_name, to_name)])
        self.event_log.append(
            {
                "event": "MOVE",
                "voter": voter.number,
                "from": from_name,
                "to": to_name,
                "t_start": t0,
                "t_end": t0 + transit,
            }
        )
        logger.info(
            "Votante %s camina de %s a %s (t=%.2f a t=%.2f)",
            voter.number,
            from_name,
            to_name,
            t0,
            t0 + transit,
        )
        self.schedule_callback(functools.partial(then, voter), after=transit)

    def _schedule_external_event(self) -> None:
        if self.last_scheduled_arrival_time is None:
            return
        trigger_time = self.random.uniform(
            0.25 * self.last_scheduled_arrival_time,
            0.75 * self.last_scheduled_arrival_time,
        )
        kind = self.random.choice(EXTERNAL_EVENT_KINDS)
        duration = self.random.uniform(3.0, 10.0)
         # Every draw above happens even when its value is about to be discarded.
        # They sit between the arrival draws and every draw the run itself makes,
        # so skipping one would shift the whole rest of the run: forcing the kind
        # has to leave the same seed producing the same people.
        if self.forced_event_time is not None:
            trigger_time = self.forced_event_time
        if self.forced_event_kind is not None:
            kind = self.forced_event_kind
        if self.forced_event_duration is not None:
            duration = self.forced_event_duration
        self.schedule_callback(
            functools.partial(self._trigger_external_event, kind, duration),
            at=trigger_time,
        )

    def _trigger_external_event(self, kind: str, duration: float) -> None:
        self.event_log.append(
            {
                "event": "EXTERNAL_EVENT",
                "kind": kind,
                "duration": duration,
                "time": self.time,
            }
        )
        logger.info(
            "EVENTO EXTERNO: %s en t=%.2f (dura %.2f min)", kind, self.time, duration
        )
        message = Message(
            sender="entorno",
            receiver=self.coordinador,
            type="EXTERNAL_EVENT",
            payload={"kind": kind, "duration": duration},
            time=self.time,
        )
        self.coordinador.receive_message(message)

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
# Length of the voting day in simulated minutes (8:00 AM -> 8:00 PM). Arrivals
# stop being admitted at this mark; voters already inside keep being served, so
# a run can finish well after it.
JORNADA_MINUTOS = 12 * 60

ARRIVAL_PROFILES = ("realista", "uniforme")

# "realista" profile: a non-homogeneous arrival process whose intensity varies
# through the day. Turnout (num_voters) is drawn i.i.d. from this density and
# sorted, i.e. an inhomogeneous Poisson process conditioned on the count.
#
#   f(t) = 0.82 * Beta((t-8)/10; 2, 4)  +  0.18 * Beta((t-8)/10; 6, 2)
#
# t in hours; (t-8)/10 maps 8:00-18:00 onto [0, 1]. The first component is the
# big mid-morning peak (Beta(2,4), mode ~11:00); the second a smaller
# late-afternoon bump (Beta(6,2), mode ~16:00). Support is 8:00-18:00, so the
# last two hours before closing are almost empty.
ARRIVAL_WINDOW_MINUTES = 10 * 60
ARRIVAL_MIXTURE = ((0.82, 2.0, 4.0), (0.18, 6.0, 2.0))
# Backwards-compatible alias for callers that referenced the older name.
CLOSING_TIME_MINUTES = JORNADA_MINUTOS
PRE_CLOSING_MINUTES = 30


class CasillaModel(Model):
    """Schedules voter arrivals and runs them through the station chain."""

    def __init__(
        self,
        num_voters: int = 200,
        arrival_rate: float = 1 / 3,
        *,
        arrival_profile: str = "realista",
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
        super().__init__(rng=rng)

        # Mesa's Model starts a hidden recurring step() event by default;
        # stop it since this model never calls step() and Mesa 3.5.1 has no
        # public API to opt out at construction time.
        self._default_schedule.stop()

        if arrival_profile not in ARRIVAL_PROFILES:
            raise ValueError(
                f"arrival_profile debe ser uno de {ARRIVAL_PROFILES}; "
                f"se recibio {arrival_profile!r}."
            )
        self.arrival_profile = arrival_profile

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

    def run_until_before_closing(
        self, minutes_before_closing: float = PRE_CLOSING_MINUTES
    ) -> None:
        """Advance to the configured pre-closing checkpoint.

        Events after the checkpoint remain queued so the caller can inspect
        the state shortly before closing and then continue with
        ``run_to_completion``.
        """
        if not 0 <= minutes_before_closing <= JORNADA_MINUTOS:
            raise ValueError("minutes_before_closing must be within the workday")

        checkpoint = JORNADA_MINUTOS - minutes_before_closing
        while (
            not self._event_list.is_empty()
            and self._event_list.peek_ahead(1)[0].time <= checkpoint
        ):
            self.run_until(self._event_list.peek_ahead(1)[0].time)
        if self.time < checkpoint:
            self.run_until(checkpoint)

    def _schedule_arrivals(self, num_voters: int, arrival_rate: float) -> None:
        if self.arrival_profile == "uniforme":
            # Homogeneous Poisson process: exponential gaps around 1/arrival_rate.
            times: list[float] = []
            t = 0.0
            for _ in range(num_voters):
                t += self.random.expovariate(arrival_rate)
                if t > JORNADA_MINUTOS:
                    break
                times.append(t)
        else:
            # Non-homogeneous: draw each arrival minute from the time-varying
            # mixture density and sort. Its support ends at 18:00 (< 20:00), so
            # the closing cutoff below never actually trims anything here.
            times = sorted(
                self._sample_realistic_arrival() for _ in range(num_voters)
            )
            times = [t for t in times if t <= JORNADA_MINUTOS]

        # Reception closes at 8:00 PM: later arrivals are turned away at the door
        # and never scheduled. Voters already admitted keep going.
        for t in times:
            self.schedule_callback(self._on_voter_arrival, at=t)
            self.last_scheduled_arrival_time = t

    def _sample_realistic_arrival(self) -> float:
        """One arrival minute drawn from the ``realista`` mixture density."""
        r = self.random.random()
        cumulative = 0.0
        alpha, beta = ARRIVAL_MIXTURE[-1][1:]
        for weight, a, b in ARRIVAL_MIXTURE:
            cumulative += weight
            if r <= cumulative:
                alpha, beta = a, b
                break
        return self.random.betavariate(alpha, beta) * ARRIVAL_WINDOW_MINUTES

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

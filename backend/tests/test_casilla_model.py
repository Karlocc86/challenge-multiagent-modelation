import functools
import logging

import pytest
from mesa.time import Priority

from casilla import CasillaModel
from casilla.agents import ADULTO_MAYOR_THRESHOLD, CANDIDATOS, Message, Station, VoterAgent
from casilla.model import TRANSIT_TIMES


def _arrivals(model: CasillaModel) -> list[tuple[int, float]]:
    return [(e["voter"], e["time"]) for e in model.event_log if e["event"] == "ARRIVAL"]


# --- Core event scheduler (schedule_callback / run_until) ---------------


def test_events_execute_in_chronological_order_regardless_of_insertion_order():
    model = CasillaModel(num_voters=0, rng=1)
    calls: list[str] = []

    model.schedule_callback(functools.partial(calls.append, "a"), at=5.0)
    model.schedule_callback(functools.partial(calls.append, "b"), at=1.0)
    model.schedule_callback(functools.partial(calls.append, "c"), at=3.0)

    model.run_until(10.0)

    assert calls == ["b", "c", "a"]


def test_clock_advances_to_exact_event_time_not_fixed_ticks():
    model = CasillaModel(num_voters=0, rng=1)
    recorded: list[float] = []

    def record() -> None:
        recorded.append(model.time)

    for t in (0.5, 2.75, 10.1):
        model.schedule_callback(record, at=t)

    model.run_until(11.0)

    assert recorded == pytest.approx([0.5, 2.75, 10.1])


def test_same_timestamp_events_respect_priority_order():
    model = CasillaModel(num_voters=0, rng=1)
    calls: list[str] = []

    model.schedule_callback(functools.partial(calls.append, "low"), at=5.0, priority=Priority.LOW)
    model.schedule_callback(functools.partial(calls.append, "high"), at=5.0, priority=Priority.HIGH)

    model.run_until(6.0)

    assert calls == ["high", "low"]


def test_run_until_boundary_leaves_later_events_unexecuted():
    model = CasillaModel(num_voters=0, rng=1)
    calls: list[str] = []

    model.schedule_callback(functools.partial(calls.append, "a"), at=1.0)
    model.schedule_callback(functools.partial(calls.append, "b"), at=5.0)
    model.schedule_callback(functools.partial(calls.append, "c"), at=9.0)

    model.run_until(5.0)

    assert calls == ["a", "b"]
    assert model.time == 5.0
    assert model._event_list.peek_ahead(1)[0].time == 9.0

    model.run_until(9.0)

    assert calls == ["a", "b", "c"]


def test_bare_lambda_callback_is_rejected():
    model = CasillaModel(num_voters=0, rng=1)

    with pytest.raises(ValueError):
        model.schedule_event(lambda: None, at=1.0)


# --- Voter arrivals -------------------------------------------------------


def test_random_arrivals_reproducible_with_seeded_rng():
    model_a = CasillaModel(num_voters=10, arrival_rate=0.3, rng=42)
    model_b = CasillaModel(num_voters=10, arrival_rate=0.3, rng=42)

    horizon = model_a.last_scheduled_arrival_time + 0.01
    model_a.run_until(horizon)
    model_b.run_until(horizon)

    assert _arrivals(model_a) == _arrivals(model_b)


def test_arrival_logs_via_logging(caplog):
    model = CasillaModel(num_voters=0, rng=1)
    model.schedule_callback(model._on_voter_arrival, at=0.5)

    with caplog.at_level(logging.INFO):
        model.run_until(1.0)

    assert "Votante 1 llega en t=0.50" in caplog.text


def test_event_log_records_each_station_completion_in_order():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    events = [e["event"] for e in model.event_log]
    assert events == [
        "ARRIVAL",
        "MOVE",
        "SECRETARIO_START",
        "SECRETARIO_DONE",
        "MOVE",
        "MESA_START",
        "MESA_DONE",
        "MOVE",
        "CASILLA_START",
        "CASILLA_DONE",
        "MOVE",
        "URNA_START",
        "URNA_DONE",
        "EXIT",
        "MOVE",
    ]

    moves = [e for e in model.event_log if e["event"] == "MOVE"]
    assert [(m["from"], m["to"]) for m in moves] == [
        ("entrada", "secretario"),
        ("secretario", "mesa"),
        ("mesa", "casilla"),
        ("casilla", "urna"),
        ("urna", "salida"),
    ]


# --- Voter demographics ----------------------------------------------------


def test_voter_edad_is_within_range_and_voto_is_a_valid_candidate():
    model = CasillaModel(num_voters=0, rng=1)
    voter = VoterAgent(model, number=1)

    assert 18 <= voter.edad <= 90
    assert voter.voto in CANDIDATOS


def test_es_adulto_mayor_matches_edad_threshold():
    model = CasillaModel(num_voters=0, rng=1)
    voter = VoterAgent(model, number=1)

    assert voter.es_adulto_mayor == (voter.edad >= ADULTO_MAYOR_THRESHOLD)


def test_arrival_event_includes_edad_and_voto():
    model = CasillaModel(num_voters=0, rng=1)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_until(0.1)

    arrival = next(e for e in model.event_log if e["event"] == "ARRIVAL")
    assert 18 <= arrival["edad"] <= 90
    assert arrival["voto"] in CANDIDATOS


# --- INE rejection branch --------------------------------------------------


def test_rejected_voter_exits_after_secretario_and_never_reaches_mesa():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=1.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    events = [e["event"] for e in model.event_log]
    assert events == ["ARRIVAL", "MOVE", "SECRETARIO_START", "SECRETARIO_DONE", "REJECTED"]

    voters = [a for a in model.agents if isinstance(a, VoterAgent)]
    assert len(voters) == 1
    assert voters[0].status == "rechazado"


def test_accepted_voter_reaches_exit_when_rejection_rate_is_zero():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    events = [e["event"] for e in model.event_log]
    assert events == [
        "ARRIVAL",
        "MOVE",
        "SECRETARIO_START",
        "SECRETARIO_DONE",
        "MOVE",
        "MESA_START",
        "MESA_DONE",
        "MOVE",
        "CASILLA_START",
        "CASILLA_DONE",
        "MOVE",
        "URNA_START",
        "URNA_DONE",
        "EXIT",
        "MOVE",
    ]


def test_station_capacity_is_configurable_per_station():
    model = CasillaModel(
        num_voters=0,
        rng=1,
        secretario_capacity=2,
        mesa_capacity=3,
        casilla_capacity=1,
        urna_capacity=1,
    )

    assert model.secretario.capacity == 2
    assert model.mesa.capacity == 3
    assert model.casilla.capacity == 1
    assert model.urna.capacity == 1


# --- Station: capacity-limited FIFO resource -------------------------------


def test_station_queues_when_busy_and_serves_fifo_on_completion():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))
    completed: list[tuple[int, float]] = []
    station.on_complete = lambda voter: completed.append((voter.number, model.time))

    # Pinned to the regular queue so this test's FIFO assertions don't
    # depend on the randomly-generated es_adulto_mayor outcome — priority
    # queueing is covered separately below.
    v1 = VoterAgent(model, number=1)
    v1.es_adulto_mayor = False
    v2 = VoterAgent(model, number=2)
    v2.es_adulto_mayor = False
    station.request(v1)
    station.request(v2)

    assert station.busy == 1
    assert list(station.queue) == [v2]

    model.run_until(1.0)
    assert completed == [(1, 1.0)]
    assert list(station.queue) == []

    model.run_until(2.0)
    assert completed == [(1, 1.0), (2, 2.0)]
    assert station.busy == 0


def test_start_service_logs_a_station_start_event():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))
    voter = VoterAgent(model, number=1)
    voter.es_adulto_mayor = False

    station.request(voter)

    starts = [e for e in model.event_log if e["event"] == "SECRETARIO_START"]
    assert starts == [
        {"event": "SECRETARIO_START", "voter": 1, "station": "secretario", "time": 0.0},
    ]


def test_station_pause_blocks_new_starts_and_resume_releases_queue():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "urna", capacity=1, service_time_range=(1.0, 1.0))
    completed: list[int] = []
    station.on_complete = lambda voter: completed.append(voter.number)

    station.receive_message(Message(sender="test", receiver=station, type="PAUSE", time=0.0))
    voter = VoterAgent(model, number=1)
    voter.es_adulto_mayor = False
    station.request(voter)

    assert station.busy == 0
    assert list(station.queue) == [voter]

    station.receive_message(Message(sender="test", receiver=station, type="RESUME", time=0.0))

    assert station.busy == 1
    assert list(station.queue) == []

    model.run_until(1.0)
    assert completed == [1]


# --- Station: preferential queue for elderly voters -------------------------


def test_elderly_voter_joins_priority_queue_instead_of_regular_queue():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))

    busy_voter = VoterAgent(model, number=1)
    busy_voter.es_adulto_mayor = False
    station.request(busy_voter)

    elderly_voter = VoterAgent(model, number=2)
    elderly_voter.es_adulto_mayor = True
    station.request(elderly_voter)

    assert list(station.queue) == []
    assert list(station.priority_queue) == [elderly_voter]


def test_elderly_voter_is_served_before_earlier_regular_voter_when_capacity_frees_up():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))
    completed: list[int] = []
    station.on_complete = lambda voter: completed.append(voter.number)

    busy_voter = VoterAgent(model, number=1)
    busy_voter.es_adulto_mayor = False
    station.request(busy_voter)

    regular_voter = VoterAgent(model, number=2)
    regular_voter.es_adulto_mayor = False
    station.request(regular_voter)

    # Arrives after regular_voter but should still be pulled first.
    elderly_voter = VoterAgent(model, number=3)
    elderly_voter.es_adulto_mayor = True
    station.request(elderly_voter)

    model.run_until(1.0)  # busy_voter's service completes, freeing one slot

    assert completed == [1]
    assert station.busy == 1  # the freed slot immediately started serving someone
    assert list(station.priority_queue) == []  # elderly_voter was pulled...
    assert list(station.queue) == [regular_voter]  # ...ahead of regular_voter

    model.run_until(2.0)  # elderly_voter's service completes next

    assert completed == [1, 3]


# --- Station: queue-join/leave events ---------------------------------------


def test_queue_join_records_queue_type_and_position():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))

    busy_voter = VoterAgent(model, number=1)
    busy_voter.es_adulto_mayor = False
    station.request(busy_voter)

    regular_voter = VoterAgent(model, number=2)
    regular_voter.es_adulto_mayor = False
    station.request(regular_voter)

    elderly_voter = VoterAgent(model, number=3)
    elderly_voter.es_adulto_mayor = True
    station.request(elderly_voter)

    joins = [e for e in model.event_log if e["event"] == "QUEUE_JOIN"]
    assert joins == [
        {
            "event": "QUEUE_JOIN",
            "voter": 2,
            "station": "secretario",
            "queue_type": "regular",
            "position": 0,
            "time": 0.0,
        },
        {
            "event": "QUEUE_JOIN",
            "voter": 3,
            "station": "secretario",
            "queue_type": "priority",
            "position": 0,
            "time": 0.0,
        },
    ]


def test_queue_leave_fires_when_pulled_and_prefers_priority_queue():
    model = CasillaModel(num_voters=0, rng=1)
    station = Station(model, "secretario", capacity=1, service_time_range=(1.0, 1.0))

    busy_voter = VoterAgent(model, number=1)
    busy_voter.es_adulto_mayor = False
    station.request(busy_voter)

    regular_voter = VoterAgent(model, number=2)
    regular_voter.es_adulto_mayor = False
    station.request(regular_voter)

    elderly_voter = VoterAgent(model, number=3)
    elderly_voter.es_adulto_mayor = True
    station.request(elderly_voter)

    model.run_until(1.0)

    leaves = [e for e in model.event_log if e["event"] == "QUEUE_LEAVE"]
    assert leaves == [
        {
            "event": "QUEUE_LEAVE",
            "voter": 3,
            "station": "secretario",
            "queue_type": "priority",
            "time": 1.0,
        },
    ]


# --- Coordinador: broadcasts the external event to every station ----------


def test_coordinador_broadcast_pauses_then_resumes_all_stations():
    model = CasillaModel(num_voters=0, rng=1)
    stations = [model.secretario, model.mesa, model.casilla, model.urna]

    message = Message(
        sender="entorno",
        receiver=model.coordinador,
        type="EXTERNAL_EVENT",
        payload={"kind": "temblor", "duration": 2.0},
        time=model.time,
    )
    model.coordinador.receive_message(message)

    assert all(s.paused for s in stations)

    model.run_until(2.0)

    assert all(not s.paused for s in stations)


def test_external_event_appears_once_in_event_log():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7)

    model.run_to_completion()

    external_events = [e for e in model.event_log if e["event"] == "EXTERNAL_EVENT"]
    assert len(external_events) == 1
    assert external_events[0]["kind"] in {"corte_de_luz", "temblor", "aguacero"}
    assert 0 < external_events[0]["time"] < model.last_scheduled_arrival_time


# --- Transit between stations -----------------------------------------------


def test_start_transit_logs_move_event_immediately():
    model = CasillaModel(num_voters=0, rng=1)
    voter = VoterAgent(model, number=1)

    model._start_transit(voter, "secretario", "mesa", lambda v: None)

    moves = [e for e in model.event_log if e["event"] == "MOVE"]
    assert len(moves) == 1
    move = moves[0]
    assert move["voter"] == 1
    assert move["from"] == "secretario"
    assert move["to"] == "mesa"
    assert move["t_start"] == 0.0
    lo, hi = TRANSIT_TIMES[("secretario", "mesa")]
    assert lo <= move["t_end"] - move["t_start"] <= hi


def test_start_transit_delays_the_callback_until_transit_completes():
    model = CasillaModel(num_voters=0, rng=1)
    voter = VoterAgent(model, number=1)
    calls: list[float] = []

    model._start_transit(voter, "secretario", "mesa", lambda v: calls.append(model.time))

    assert calls == []  # scheduled, not fired synchronously

    move = next(e for e in model.event_log if e["event"] == "MOVE")
    model.run_until(move["t_end"])

    assert calls == [move["t_end"]]


# ---------------------------------------------------------------------------
# Evento externo forzado
# ---------------------------------------------------------------------------


def test_forced_external_event_uses_the_requested_kind_time_and_duration():
    model = CasillaModel(
        num_voters=5,
        arrival_rate=0.5,
        rng=7,
        forced_event_kind="aguacero",
        forced_event_time=4.0,
        forced_event_duration=20.0,
    )

    model.run_to_completion()

    external = [e for e in model.event_log if e["event"] == "EXTERNAL_EVENT"]
    assert len(external) == 1
    assert external[0]["kind"] == "aguacero"
    assert external[0]["time"] == pytest.approx(4.0)
    assert external[0]["duration"] == pytest.approx(20.0)


def test_forcing_only_the_kind_leaves_the_rest_of_the_run_identical():
    # The three draws in _schedule_external_event sit between the arrival draws
    # and every draw the run itself makes, so overwriting their result must not
    # shift anything downstream: same seed, same people, different weather.
    def run(**forced):
        model = CasillaModel(num_voters=20, arrival_rate=0.5, rng=11, **forced)
        model.run_to_completion()
        return model.event_log

    random_kind = run()
    forced_kind = run(forced_event_kind="temblor")

    without_external = lambda log: [e for e in log if e["event"] != "EXTERNAL_EVENT"]
    assert without_external(random_kind) == without_external(forced_kind)

    before = next(e for e in random_kind if e["event"] == "EXTERNAL_EVENT")
    after = next(e for e in forced_kind if e["event"] == "EXTERNAL_EVENT")
    assert after["kind"] == "temblor"
    assert after["time"] == pytest.approx(before["time"])
    assert after["duration"] == pytest.approx(before["duration"])


def test_forced_event_time_can_fall_outside_the_random_window():
    # Unforced the event lands between 25% and 75% of the arrival horizon; a
    # forced minute is not held to that window.
    model = CasillaModel(
        num_voters=5, arrival_rate=0.5, rng=7, forced_event_time=500.0
    )

    model.run_to_completion()

    external = next(e for e in model.event_log if e["event"] == "EXTERNAL_EVENT")
    assert external["time"] == pytest.approx(500.0)
    assert external["time"] > model.last_scheduled_arrival_time


def test_forced_external_event_is_reproducible_with_seeded_rng():
    def run():
        model = CasillaModel(
            num_voters=10,
            arrival_rate=0.5,
            rng=42,
            forced_event_kind="corte_de_luz",
            forced_event_time=3.0,
        )
        model.run_to_completion()
        return model.event_log

    assert run() == run()


def test_no_external_event_without_arrivals_even_when_forced():
    # No arrivals means no horizon to place the event in, and several tests rely
    # on a model with num_voters=0 producing a clean log.
    model = CasillaModel(
        num_voters=0, rng=1, forced_event_kind="temblor", forced_event_time=1.0
    )

    model.run_to_completion()

    assert not [e for e in model.event_log if e["event"] == "EXTERNAL_EVENT"]

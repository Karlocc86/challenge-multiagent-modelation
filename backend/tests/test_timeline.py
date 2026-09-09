import pytest

from casilla import CasillaModel
from casilla.agents import VoterAgent
from casilla.timeline import build_timeline


def test_summary_reports_duration_and_voter_counts():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)

    assert timeline["summary"]["duration_minutes"] == pytest.approx(model.time)
    assert timeline["summary"]["voters_arrived"] == 5
    assert timeline["summary"]["voters_exited"] == 5
    assert timeline["summary"]["voters_rejected"] == 0


def test_summary_includes_station_configuration():
    model = CasillaModel(num_voters=0, rng=1, secretario_capacity=2)
    model.run_to_completion()

    timeline = build_timeline(model)

    assert timeline["summary"]["stations"]["secretario"] == {
        "capacity": 2,
        "service_time_range": [1.5, 2.5],
    }
    assert timeline["summary"]["stations"]["urna"] == {
        "capacity": 1,
        "service_time_range": [0.2, 0.6],
    }


def test_movements_strip_event_key_and_keep_the_rest():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    timeline = build_timeline(model)

    assert len(timeline["movements"]) == 5
    first = timeline["movements"][0]
    assert set(first.keys()) == {"voter", "from", "to", "t_start", "t_end"}
    assert first["from"] == "entrada"
    assert first["to"] == "secretario"


def test_queue_events_pair_join_and_leave():
    model = CasillaModel(num_voters=0, rng=1)
    station = model.secretario

    busy_voter = VoterAgent(model, number=1)
    busy_voter.es_adulto_mayor = False
    station.request(busy_voter)

    waiting_voter = VoterAgent(model, number=2)
    waiting_voter.es_adulto_mayor = False
    station.request(waiting_voter)

    model.run_to_completion()

    # Filtered to secretario: voter 1 always starts immediately there (never
    # queues), so exactly one queue visit is guaranteed at this station
    # regardless of what randomly happens downstream at mesa/casilla/urna.
    join = next(
        e for e in model.event_log if e["event"] == "QUEUE_JOIN" and e["station"] == "secretario"
    )
    leave = next(
        e for e in model.event_log if e["event"] == "QUEUE_LEAVE" and e["station"] == "secretario"
    )

    timeline = build_timeline(model)

    secretario_queue_events = [
        e for e in timeline["queue_events"] if e["station"] == "secretario"
    ]
    assert secretario_queue_events == [
        {
            "voter": 2,
            "station": "secretario",
            "queue_type": "regular",
            "position": 0,
            "t_join": join["time"],
            "t_leave": leave["time"],
        },
    ]


def test_station_events_rename_start_and_done_to_service_start_and_end():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    timeline = build_timeline(model)

    secretario_events = [
        e for e in timeline["station_events"] if e["station"] == "secretario"
    ]
    assert [e["event"] for e in secretario_events] == ["SERVICE_START", "SERVICE_END"]
    assert all(set(e.keys()) == {"voter", "station", "event", "t"} for e in secretario_events)


def test_voter_events_exclude_edad_and_voto():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    timeline = build_timeline(model)

    arrival = next(e for e in timeline["voter_events"] if e["event"] == "ARRIVAL")
    assert set(arrival.keys()) == {"voter", "event", "t"}


def test_external_events_rename_time_to_t_start():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7)

    model.run_to_completion()

    timeline = build_timeline(model)

    assert len(timeline["external_events"]) == 1
    external_event = timeline["external_events"][0]
    assert set(external_event.keys()) == {"kind", "t_start", "duration"}

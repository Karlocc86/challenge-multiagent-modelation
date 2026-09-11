import pytest

from casilla import CasillaModel
from casilla.agents import CANDIDATOS, VoterAgent
from casilla.timeline import build_timeline


def test_summary_reports_duration_and_voter_counts():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)

    assert timeline["summary"]["duration_minutes"] == pytest.approx(model.time)
    assert timeline["summary"]["voters_arrived"] == 5
    assert timeline["summary"]["voters_exited"] == 5
    assert timeline["summary"]["voters_rejected"] == 0


def test_summary_exposes_workday_length_and_overtime():
    # 1200 voters at rate 1.0 overruns minute 720, so the last person exits
    # in overtime and duration_minutes lands past the close.
    model = CasillaModel(num_voters=1200, arrival_rate=1.0, rng=7)
    model.run_to_completion()

    summary = build_timeline(model)["summary"]
    last_arrival = max(
        e["time"] for e in model.event_log if e["event"] == "ARRIVAL"
    )

    assert summary["jornada_minutes"] == 720
    assert summary["reception_closed_at"] == 720
    assert summary["last_arrival_minute"] == pytest.approx(last_arrival)
    assert summary["last_arrival_minute"] <= 720
    assert summary["overtime_minutes"] == pytest.approx(summary["duration_minutes"] - 720)
    assert summary["overtime_minutes"] > 0


def test_summary_overtime_is_zero_and_last_arrival_null_without_arrivals():
    model = CasillaModel(num_voters=0, rng=1)
    model.run_to_completion()

    summary = build_timeline(model)["summary"]

    assert summary["jornada_minutes"] == 720
    assert summary["last_arrival_minute"] is None
    assert summary["overtime_minutes"] == 0


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


def test_voter_events_carry_the_candidate_on_exit():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)
    exits = [e for e in timeline["voter_events"] if e["event"] == "EXIT"]

    assert exits
    assert all(e["candidate"] in CANDIDATOS for e in exits)
    # the per-EXIT candidates add up to the same tally build_timeline reports
    from collections import Counter

    counted = Counter(e["candidate"] for e in exits)
    expected = timeline["summary"]["results"]["votes_by_candidate"]
    assert {c: counted.get(c, 0) for c in expected} == expected


def test_external_events_rename_time_to_t_start():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7)

    model.run_to_completion()

    timeline = build_timeline(model)

    assert len(timeline["external_events"]) == 1
    external_event = timeline["external_events"][0]
    assert set(external_event.keys()) == {"kind", "t_start", "duration"}


def test_results_tally_only_counts_voters_who_exited():
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)
    results = timeline["summary"]["results"]

    assert results["total_votes"] == 5
    assert sum(results["votes_by_candidate"].values()) == 5


def test_results_exclude_rejected_voters():
    # Everyone is turned away at the secretario, so no ballot is ever cast
    # even though each voter arrived carrying one.
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=1.0)
    model.run_to_completion()

    timeline = build_timeline(model)
    results = timeline["summary"]["results"]

    assert timeline["summary"]["voters_arrived"] == 5
    assert timeline["summary"]["voters_rejected"] == 5
    assert results["total_votes"] == 0
    assert set(results["votes_by_candidate"].values()) == {0}
    assert results["winner"] is None
    assert results["is_tie"] is False
    assert results["tied_candidates"] == []


def test_results_report_every_candidate_even_at_zero_votes():
    model = CasillaModel(num_voters=1, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)
    results = timeline["summary"]["results"]

    assert set(results["votes_by_candidate"]) == set(CANDIDATOS)
    assert results["total_votes"] == 1


def test_results_name_the_candidate_with_the_most_votes():
    # Named off CANDIDATOS instead of literals so renaming the parties does
    # not break the test.
    first, second, third = CANDIDATOS[0], CANDIDATOS[1], CANDIDATOS[2]
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()
    # Overwrite the ballots after the fact so the tally is deterministic
    # regardless of how the RNG handed out votes during the run.
    arrivals = [e for e in model.event_log if e["event"] == "ARRIVAL"]
    for entry, candidate in zip(arrivals, [first, first, first, second, third]):
        entry["voto"] = candidate

    results = build_timeline(model)["summary"]["results"]

    assert results["votes_by_candidate"] == {first: 3, second: 1, third: 1}
    assert results["winner"] == first
    assert results["is_tie"] is False
    assert results["tied_candidates"] == []


def test_results_report_a_tie_instead_of_picking_a_winner():
    first, second, third = CANDIDATOS[0], CANDIDATOS[1], CANDIDATOS[2]
    model = CasillaModel(num_voters=4, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()
    arrivals = [e for e in model.event_log if e["event"] == "ARRIVAL"]
    for entry, candidate in zip(arrivals, [first, first, second, second]):
        entry["voto"] = candidate

    results = build_timeline(model)["summary"]["results"]

    assert results["votes_by_candidate"] == {first: 2, second: 2, third: 0}
    assert results["winner"] is None
    assert results["is_tie"] is True
    assert results["tied_candidates"] == sorted([first, second])

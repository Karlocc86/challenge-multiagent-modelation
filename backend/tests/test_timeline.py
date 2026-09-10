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


def test_voter_events_expose_edad_voto_and_adulto_mayor_on_arrival():
    model = CasillaModel(num_voters=0, rng=1, rejection_rate=0.0)
    model.schedule_callback(model._on_voter_arrival, at=0.1)

    model.run_to_completion()

    timeline = build_timeline(model)

    arrival = next(e for e in timeline["voter_events"] if e["event"] == "ARRIVAL")
    assert set(arrival.keys()) == {
        "voter",
        "event",
        "t",
        "edad",
        "es_adulto_mayor",
        "voto",
    }
    logged = next(e for e in model.event_log if e["event"] == "ARRIVAL")
    assert arrival["edad"] == logged["edad"]
    assert arrival["voto"] == logged["voto"]
    assert arrival["es_adulto_mayor"] == (arrival["edad"] >= 60)


def test_voter_events_keep_exit_and_rejected_minimal():
    # edad and voto live only on the ARRIVAL entry, so the other events report
    # three keys rather than padding two nulls.
    model = CasillaModel(num_voters=5, arrival_rate=0.5, rng=7, rejection_rate=0.0)
    model.run_to_completion()

    timeline = build_timeline(model)

    others = [e for e in timeline["voter_events"] if e["event"] != "ARRIVAL"]
    assert others
    assert all(set(e.keys()) == {"voter", "event", "t"} for e in others)


def test_rejected_voter_still_reports_its_details_on_arrival():
    # A voter turned away at the secretario never votes, but the panel still has
    # to be able to label them.
    model = CasillaModel(num_voters=3, arrival_rate=0.5, rng=7, rejection_rate=1.0)
    model.run_to_completion()

    timeline = build_timeline(model)

    assert any(e["event"] == "REJECTED" for e in timeline["voter_events"])
    arrival = next(e for e in timeline["voter_events"] if e["event"] == "ARRIVAL")
    assert arrival["voto"] in CANDIDATOS
    assert 18 <= arrival["edad"] <= 90


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

"""Serializes a finished CasillaModel run into the JSON contract Unity
consumes from ``POST /simulate``.

Keeps event_log's internal event names/keys out of model.py, which stays
unaware of JSON or HTTP; this module is the only place that renames or
reshapes anything for the wire format.
"""

from __future__ import annotations

from typing import Any

from .agents import CANDIDATOS
from .model import CasillaModel

STATION_NAMES = ["secretario", "mesa", "casilla", "urna"]


def build_timeline(model: CasillaModel) -> dict[str, Any]:
    return {
        "summary": _build_summary(model),
        "movements": _build_movements(model),
        "queue_events": _build_queue_events(model),
        "station_events": _build_station_events(model),
        "voter_events": _build_voter_events(model),
        "external_events": _build_external_events(model),
    }


def _build_summary(model: CasillaModel) -> dict[str, Any]:
    event_types = [entry["event"] for entry in model.event_log]
    return {
        "duration_minutes": model.time,
        "voters_arrived": event_types.count("ARRIVAL"),
        "voters_exited": event_types.count("EXIT"),
        "voters_rejected": event_types.count("REJECTED"),
        "results": _build_results(model),
        "stations": {
            name: {
                "capacity": getattr(model, name).capacity,
                "service_time_range": list(getattr(model, name).service_time_range),
            }
            for name in STATION_NAMES
        },
    }


def _build_results(model: CasillaModel) -> dict[str, Any]:
    """Tally the votes actually cast and decide the winner.

    A ballot only counts once its voter clears the urna and leaves, so we
    tally the EXIT events rather than the ARRIVAL ones: a voter rejected at
    the secretario carries a ``voto`` but never deposits it, and one still
    inside the casilla when the run ends has not deposited it yet.

    Every name in CANDIDATOS is reported even at zero votes, so the shape of
    ``votes_by_candidate`` stays stable for clients regardless of the run.
    """
    # `voto` is only written on the ARRIVAL entry, so the ballot has to be
    # looked up by voter number when that voter's EXIT shows up.
    ballots = {
        entry["voter"]: entry["voto"]
        for entry in model.event_log
        if entry["event"] == "ARRIVAL"
    }

    votes = dict.fromkeys(CANDIDATOS, 0)
    for entry in model.event_log:
        if entry["event"] != "EXIT":
            continue
        # A voter placed straight into a station (as tests do) never logged
        # an ARRIVAL, so there is no ballot to count rather than a crash.
        candidate = ballots.get(entry["voter"])
        if candidate is None:
            continue
        # A candidate dropped from CANDIDATOS mid-run would not be pre-seeded.
        votes[candidate] = votes.get(candidate, 0) + 1

    total = sum(votes.values())
    top = max(votes.values(), default=0)
    # With no votes at all there is nothing to win, so the leaders list stays
    # empty instead of naming every candidate as tied at zero.
    leaders = (
        sorted(name for name, count in votes.items() if count == top) if total else []
    )
    is_tie = len(leaders) > 1

    return {
        "votes_by_candidate": votes,
        "total_votes": total,
        "winner": None if is_tie or not leaders else leaders[0],
        "is_tie": is_tie,
        "tied_candidates": leaders if is_tie else [],
    }


def _build_movements(model: CasillaModel) -> list[dict[str, Any]]:
    return [
        {
            "voter": entry["voter"],
            "from": entry["from"],
            "to": entry["to"],
            "t_start": entry["t_start"],
            "t_end": entry["t_end"],
        }
        for entry in model.event_log
        if entry["event"] == "MOVE"
    ]


def _build_queue_events(model: CasillaModel) -> list[dict[str, Any]]:
    # A voter only ever queues once per station in their whole journey, so
    # matching a QUEUE_LEAVE by (voter, station) is unambiguous. Every
    # QUEUE_JOIN is guaranteed a match by the time run_to_completion() has
    # drained the event queue.
    leave_times = {
        (entry["voter"], entry["station"]): entry["time"]
        for entry in model.event_log
        if entry["event"] == "QUEUE_LEAVE"
    }
    return [
        {
            "voter": entry["voter"],
            "station": entry["station"],
            "queue_type": entry["queue_type"],
            "position": entry["position"],
            "t_join": entry["time"],
            "t_leave": leave_times[(entry["voter"], entry["station"])],
        }
        for entry in model.event_log
        if entry["event"] == "QUEUE_JOIN"
    ]


def _build_station_events(model: CasillaModel) -> list[dict[str, Any]]:
    rename = {}
    for name in STATION_NAMES:
        rename[f"{name.upper()}_START"] = "SERVICE_START"
        rename[f"{name.upper()}_DONE"] = "SERVICE_END"
    return [
        {
            "voter": entry["voter"],
            "station": entry["station"],
            "event": rename[entry["event"]],
            "t": entry["time"],
        }
        for entry in model.event_log
        if entry["event"] in rename
    ]


def _build_voter_events(model: CasillaModel) -> list[dict[str, Any]]:
    return [
        {"voter": entry["voter"], "event": entry["event"], "t": entry["time"]}
        for entry in model.event_log
        if entry["event"] in ("ARRIVAL", "REJECTED", "EXIT")
    ]


def _build_external_events(model: CasillaModel) -> list[dict[str, Any]]:
    return [
        {"kind": entry["kind"], "t_start": entry["time"], "duration": entry["duration"]}
        for entry in model.event_log
        if entry["event"] == "EXTERNAL_EVENT"
    ]

"""Flask API exposing the casilla simulation.

``POST /simulate`` runs the whole simulation instantly server-side and hands
back the full timeline in one response (the "precomputed replay" the Unity
client consumes). ``GET /`` serves a self-contained results dashboard for one
fixed run, for presenting the simulation.
"""

import json
import math
from pathlib import Path

from flask import Flask, Response, jsonify, request

from casilla import CasillaModel
from casilla.model import ARRIVAL_PROFILES, EXTERNAL_EVENT_KINDS
from casilla.timeline import build_timeline

app = Flask(__name__)

_DASHBOARD_FILE = Path(__file__).with_name("static") / "dashboard.html"
_DASHBOARD_KEYS = ("summary", "voter_events", "queue_events", "external_events")

# Last run served by POST /simulate (e.g. the one the Unity client just played).
# GET / shows this by default so "finish in Unity, open the page" shows *that*
# run's vote results. Lives only in memory; a query string on GET / overrides it.
_LAST_RUN: dict | None = None

# Real casilla this project is calibrated against (PREP 2024, Sección 3068 /
# Casilla Contigua 5, Puerta de Hierro / Paseo Andares, Zapopan): 701 electores
# were EXPECTED (lista nominal) and 469 actually ARRIVED and voted (66.9%
# participación real). `num_voters` below models the ones who arrive — the 469,
# not the 701 — since this model has no "expected but never showed up" category.
LISTA_NOMINAL_REAL = 701
PARTICIPACION_REAL = 0.6690

# Fixed scenario shown on GET / — capacities tuned so the run closes ~17:48-
# 19:46 with ~464-469 votos, matching the real closing time and turnout. Any
# key is overridable via query string, e.g. /?seed=1&num_voters=300.
DEFAULT_SCENARIO = {
    "num_voters": round(LISTA_NOMINAL_REAL * PARTICIPACION_REAL),  # 469 llegaron (no 701)
    # Only used if arrival_profile is switched to "uniforme" (realista ignores
    # it). Calibrated so ~469 reliably arrive before the 8 PM cutoff.
    "arrival_rate": 0.78,
    "arrival_profile": "realista",
    "seed": 7,
    "secretario_capacity": 2,
    "mesa_capacity": 1,
    "casilla_capacity": 3,
    "urna_capacity": 1,
    "rejection_rate": 0.01,
}
_INT_KEYS = (
    "num_voters",
    "seed",
    "secretario_capacity",
    "mesa_capacity",
    "casilla_capacity",
    "urna_capacity",
)
_FLOAT_KEYS = ("arrival_rate", "rejection_rate")
_STR_KEYS = ("arrival_profile",)


class InvalidParameter(ValueError):
    """A request parameter failed validation; reported to the client as HTTP 400."""


def _positive_number(params, key, default):
    """Read a strictly positive, finite number from the request body.

    Messages are in Spanish because they surface in Unity's console, where the
    person adjusting the inspector reads them.
    """
    # An explicit null reads as "not sent" rather than blowing up, since clients
    # that serialize empty fields would otherwise get a 400 for leaving a blank.
    if key not in params or params[key] is None:
        return default
    value = params[key]
    # isinstance(True, int) is True in Python, so bools need rejecting by name.
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise InvalidParameter(f"{key} debe ser un numero; se recibio {value!r}.")
    if not math.isfinite(value):
        raise InvalidParameter(f"{key} debe ser un numero finito; se recibio {value!r}.")
    if value <= 0:
        raise InvalidParameter(f"{key} debe ser mayor que 0; se recibio {value!r}.")
    return float(value)


def _arrival_profile(params):
    value = params.get("arrival_profile")
    if value is None:
        return "realista"
    if value not in ARRIVAL_PROFILES:
        raise InvalidParameter(
            f"arrival_profile debe ser uno de: {', '.join(ARRIVAL_PROFILES)}; "
            f"se recibio {value!r}."
        )
    return value


def _event_kind(params, key):
    """Read an optional external-event kind, restricted to the known set.

    Comparing against the list also catches numbers, lists and objects, so no
    separate type check is needed.
    """
    if key not in params or params[key] is None:
        return None
    value = params[key]
    if value not in EXTERNAL_EVENT_KINDS:
        raise InvalidParameter(
            f"{key} debe ser uno de: {', '.join(EXTERNAL_EVENT_KINDS)}; "
            f"se recibio {value!r}."
        )
    return value

@app.route("/simulate", methods=["POST"])
def simulate():
    params = request.get_json(silent=True)
    if params is None:
        params = {}
    if not isinstance(params, dict):
        return jsonify({"error": "El cuerpo JSON debe ser un objeto."}), 400
    try:
        arrival_rate = _positive_number(params, "arrival_rate", 1 / 3)
        arrival_profile = _arrival_profile(params)
        forced_event_kind = _event_kind(params, "forced_event_kind")
        forced_event_time = _positive_number(params, "forced_event_time", None)
        forced_event_duration = _positive_number(params, "forced_event_duration", None)
        beta_mixture = params.get("arrival_beta")
        if beta_mixture is not None:
            beta_mixture = validate_beta_mixture(beta_mixture)
        arrival_rate = (
            _positive_number(params, "arrival_rate", 1 / 3)
            if beta_mixture is None
            else 1 / 3
        )
        count = params.get("num_voters", 200)
        if isinstance(count, bool) or not isinstance(count, int) or count < 0:
            raise ValueError("num_voters debe ser un entero mayor o igual que 0.")
    except ValueError as exc:
        return jsonify({"error": str(exc)}), 400

    model = CasillaModel(
        num_voters=count,
        arrival_rate=arrival_rate,
        arrival_profile=arrival_profile,
        secretario_capacity=params.get("secretario_capacity", 1),
        mesa_capacity=params.get("mesa_capacity", 1),
        casilla_capacity=params.get("casilla_capacity", 1),
        urna_capacity=params.get("urna_capacity", 1),
        rejection_rate=params.get("rejection_rate", 0.02),
        forced_event_kind=forced_event_kind,
        forced_event_time=forced_event_time,
        forced_event_duration=forced_event_duration,
        rng=params.get("seed"),
    )
    model.run_to_completion()
    timeline = build_timeline(model)

    global _LAST_RUN
    _LAST_RUN = {
        "params": {
            "num_voters": params.get("num_voters", 200),
            "arrival_rate": arrival_rate,
            "arrival_profile": arrival_profile,
            "seed": params.get("seed"),
            "rejection_rate": params.get("rejection_rate", 0.02),
            "secretario_capacity": params.get("secretario_capacity", 1),
            "mesa_capacity": params.get("mesa_capacity", 1),
            "casilla_capacity": params.get("casilla_capacity", 1),
            "urna_capacity": params.get("urna_capacity", 1),
        },
        "timeline": {key: timeline[key] for key in _DASHBOARD_KEYS},
    }
    return jsonify(timeline)


def _run_scenario(scenario):
    model = CasillaModel(
        num_voters=scenario["num_voters"],
        arrival_rate=scenario["arrival_rate"],
        arrival_profile=scenario["arrival_profile"],
        secretario_capacity=scenario["secretario_capacity"],
        mesa_capacity=scenario["mesa_capacity"],
        casilla_capacity=scenario["casilla_capacity"],
        urna_capacity=scenario["urna_capacity"],
        rejection_rate=scenario["rejection_rate"],
        rng=scenario["seed"],
    )
    model.run_to_completion()
    timeline = build_timeline(model)
    return {"params": scenario, "timeline": {k: timeline[k] for k in _DASHBOARD_KEYS}}


@app.route("/")
def dashboard():
    """Results dashboard.

    Shows the last run played through POST /simulate (the Unity run) if there is
    one; otherwise a demo scenario. A query string (?seed=1&num_voters=300&...)
    always forces a fresh run with those parameters instead.
    """
    overrides = [k for k in (*_INT_KEYS, *_FLOAT_KEYS, *_STR_KEYS) if k in request.args]
    if not overrides and _LAST_RUN is not None:
        payload_obj = {**_LAST_RUN, "live": True}
    else:
        scenario = dict(DEFAULT_SCENARIO)
        try:
            for key in _INT_KEYS:
                if key in request.args:
                    scenario[key] = int(request.args[key])
            for key in _FLOAT_KEYS:
                if key in request.args:
                    scenario[key] = float(request.args[key])
            if "arrival_profile" in request.args:
                scenario["arrival_profile"] = _arrival_profile(request.args)
        except (ValueError, InvalidParameter) as exc:
            return jsonify({"error": f"parametro invalido en la URL: {exc}"}), 400
        payload_obj = {**_run_scenario(scenario), "live": False}

    payload = json.dumps(payload_obj)
    html = _DASHBOARD_FILE.read_text(encoding="utf-8").replace("/*__DATA__*/null", payload)
    return Response(html, mimetype="text/html")


if __name__ == "__main__":
    app.run(debug=True)

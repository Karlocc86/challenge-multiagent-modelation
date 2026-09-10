"""Flask API exposing the casilla simulation to Unity.

A single endpoint, matching the "precomputed replay" design: run the whole
simulation instantly server-side and hand Unity the full timeline in one
response, rather than stepping the model forward per request.
"""

import math

from flask import Flask, jsonify, request

from casilla import CasillaModel
from casilla.arrivals import validate_beta_mixture
from casilla.timeline import build_timeline

app = Flask(__name__)


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


@app.route("/simulate", methods=["POST"])
def simulate():
    params = request.get_json(silent=True)
    if params is None:
        params = {}
    if not isinstance(params, dict):
        return jsonify({"error": "El cuerpo JSON debe ser un objeto."}), 400
    try:
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
        arrival_beta=beta_mixture,
        secretario_capacity=params.get("secretario_capacity", 1),
        mesa_capacity=params.get("mesa_capacity", 1),
        casilla_capacity=params.get("casilla_capacity", 1),
        urna_capacity=params.get("urna_capacity", 1),
        rejection_rate=params.get("rejection_rate", 0.02),
        rng=params.get("seed"),
    )
    model.run_to_completion()
    return jsonify(build_timeline(model))


if __name__ == "__main__":
    app.run(debug=True)

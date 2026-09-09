"""Flask API exposing the casilla simulation to Unity.

A single endpoint, matching the "precomputed replay" design: run the whole
simulation instantly server-side and hand Unity the full timeline in one
response, rather than stepping the model forward per request.
"""

from flask import Flask, jsonify, request

from casilla import CasillaModel
from casilla.timeline import build_timeline

app = Flask(__name__)


@app.route("/simulate", methods=["POST"])
def simulate():
    params = request.get_json(silent=True) or {}
    model = CasillaModel(
        num_voters=params.get("num_voters", 200),
        arrival_rate=params.get("arrival_rate", 1 / 3),
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

import random
from pathlib import Path

import pytest

from casilla.arrivals import sample_beta_arrivals, validate_beta_mixture
from server import app


def beta_formula():
    return {"start_hour": 8, "end_hour": 18, "components": [
        {"weight": 0.82, "alpha": 2, "beta": 4},
        {"weight": 0.18, "alpha": 6, "beta": 2},
    ]}


def test_beta_formula_is_exact_sorted_reproducible_and_inside_window():
    config = validate_beta_mixture(beta_formula())
    first = sample_beta_arrivals(random.Random(7), 1400, config)
    second = sample_beta_arrivals(random.Random(7), 1400, config)
    assert first == second
    assert len(first) == 1400
    assert first == sorted(first)
    assert 0 <= first[0] < first[-1] < 600


def test_beta_formula_has_expected_mean_and_morning_peak():
    times = sample_beta_arrivals(
        random.Random(42), 100000, validate_beta_mixture(beta_formula())
    )
    # E[X] = .82*(2/6) + .18*(6/8) = .408333..., scaled to 600 min.
    assert abs(sum(times) / len(times) - 245) < 2
    hourly = [sum(hour * 60 <= t < (hour + 1) * 60 for t in times) for hour in range(10)]
    assert hourly.index(max(hourly)) == 2


def test_beta_formula_runs_through_http_api():
    params = {"num_voters": 200, "seed": 7, "arrival_beta": beta_formula()}
    response = app.test_client().post("/simulate", json=params)
    assert response.status_code == 200
    timeline = response.get_json()
    assert timeline["summary"]["arrival_beta"] == validate_beta_mixture(beta_formula())
    arrivals = [event["t"] for event in timeline["voter_events"] if event["event"] == "ARRIVAL"]
    assert len(arrivals) == 200
    assert all(0 <= time < 600 for time in arrivals)


def test_omitting_beta_preserves_original_poisson_mode():
    params = {"num_voters": 20, "arrival_rate": 0.5, "seed": 7}
    first = app.test_client().post("/simulate", json=params)
    second = app.test_client().post("/simulate", json=params)
    assert first.status_code == 200
    assert first.get_json() == second.get_json()
    assert first.get_json()["summary"]["arrival_beta"] is None


@pytest.mark.parametrize("config", [
    {},
    {"start_hour": 18, "end_hour": 8, "components": []},
    {"start_hour": 8, "end_hour": 18, "components": []},
    {"start_hour": 8, "end_hour": 18, "components": [
        {"weight": .8, "alpha": 2, "beta": 4},
        {"weight": .1, "alpha": 6, "beta": 2},
    ]},
    {"start_hour": 8, "end_hour": 18, "components": [
        {"weight": 1, "alpha": 0, "beta": 4},
    ]},
])
def test_invalid_beta_formula_is_rejected(config):
    with pytest.raises(ValueError):
        validate_beta_mixture(config)
    response = app.test_client().post("/simulate", json={"arrival_beta": config})
    assert response.status_code == 400


def test_actual_unity_scene_uses_only_beta_formula():
    scene = (
        Path(__file__).parents[2]
        / "unity-client/MultiAgent-simulation/Assets/Scenes/Casilla.unity"
    ).read_text(encoding="utf-8")
    assert "usarFormulaBeta: 1" in scene
    assert "horaFinBeta: 18" in scene
    assert "usarLlegadasPorHorario" not in scene
    assert "franjasLlegadas" not in scene
    assert "mostrarGraficaLlegadas: 1" in scene


def test_unity_has_simple_arrivals_chart_and_simulation_inputs():
    script = (
        Path(__file__).parents[2]
        / "unity-client/MultiAgent-simulation/Assets/Scripts/SimulationRunner.cs"
    ).read_text(encoding="utf-8")
    assert "void DibujarGraficaLlegadas()" in script
    assert "Llegadas por hora (corrida completa)" in script
    assert "Semilla:" in script
    assert "Capacidades S/M/C/U:" in script
    assert "Evento externo:" in script

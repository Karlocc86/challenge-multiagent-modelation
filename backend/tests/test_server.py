import pytest

from casilla import CasillaModel
from server import app


def test_dashboard_route_serves_html_with_the_run_embedded():
    client = app.test_client()

    response = client.get("/")

    assert response.status_code == 200
    assert response.mimetype == "text/html"
    body = response.get_data(as_text=True)
    assert "Resultados de la simulación" in body
    # the /*__DATA__*/null placeholder was replaced with real JSON
    assert 'window.__DATA__ = {"params"' in body


def test_dashboard_route_accepts_query_overrides():
    client = app.test_client()

    response = client.get("/?seed=1&num_voters=40")

    assert response.status_code == 200
    assert '"num_voters": 40' in response.get_data(as_text=True)


def test_dashboard_route_rejects_a_non_numeric_query_param():
    client = app.test_client()

    response = client.get("/?num_voters=abc")

    assert response.status_code == 400


def test_simulate_rejects_an_unknown_arrival_profile():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 5, "seed": 7, "arrival_profile": "loco"}
    )

    assert response.status_code == 400
    assert "arrival_profile" in response.get_json()["error"]


def test_simulate_defaults_to_the_realista_arrival_profile():
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 300, "seed": 7})

    assert response.status_code == 200
    arrivals = [
        e["t"] for e in response.get_json()["voter_events"] if e["event"] == "ARRIVAL"
    ]
    # realista draws all arrivals inside the 8:00-18:00 window (<= 600 min)
    assert arrivals and max(arrivals) <= 600


def test_dashboard_shows_the_last_simulate_run_by_default():
    client = app.test_client()

    client.post("/simulate", json={"num_voters": 17, "seed": 5})
    body = client.get("/").get_data(as_text=True)

    assert '"num_voters": 17' in body
    assert '"live": true' in body


def test_dashboard_query_string_forces_a_fresh_run_over_the_cached_one():
    client = app.test_client()

    client.post("/simulate", json={"num_voters": 17, "seed": 5})
    body = client.get("/?num_voters=9").get_data(as_text=True)

    assert '"num_voters": 9' in body
    assert '"live": false' in body


def test_simulate_returns_full_contract_shape():
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 5, "arrival_rate": 0.5, "seed": 7})

    assert response.status_code == 200
    body = response.get_json()
    assert set(body.keys()) == {
        "summary",
        "movements",
        "queue_events",
        "station_events",
        "voter_events",
        "external_events",
    }


def test_simulate_applies_requested_num_voters():
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 3, "seed": 1})

    assert response.status_code == 200
    body = response.get_json()
    assert body["summary"]["voters_arrived"] == 3


def test_simulate_defaults_to_200_voters_when_no_body_given():
    client = app.test_client()

    response = client.post("/simulate")

    assert response.status_code == 200
    body = response.get_json()
    assert body["summary"]["voters_arrived"] == 200


def test_simulate_applies_requested_arrival_rate():
    # Compared against a model built by hand rather than an approximate shape:
    # with the same seed the two runs must land on the same simulated clock.
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 20, "arrival_rate": 2.0, "seed": 11}
    )

    expected = CasillaModel(num_voters=20, arrival_rate=2.0, rng=11)
    expected.run_to_completion()
    assert response.status_code == 200
    body = response.get_json()
    assert body["summary"]["duration_minutes"] == pytest.approx(expected.time)


def test_simulate_defaults_to_one_third_arrival_rate_when_not_given():
    # Guards the promise that a client which omits the field keeps the exact
    # behaviour it had before the field existed.
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 20, "seed": 11})

    expected = CasillaModel(num_voters=20, arrival_rate=1 / 3, rng=11)
    expected.run_to_completion()
    assert response.status_code == 200
    body = response.get_json()
    assert body["summary"]["duration_minutes"] == pytest.approx(expected.time)


def test_simulate_rejects_zero_arrival_rate():
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 5, "arrival_rate": 0, "seed": 7})

    assert response.status_code == 400
    assert "arrival_rate" in response.get_json()["error"]


def test_simulate_rejects_negative_arrival_rate():
    client = app.test_client()

    response = client.post("/simulate", json={"num_voters": 5, "arrival_rate": -1.5, "seed": 7})

    assert response.status_code == 400
    assert "arrival_rate" in response.get_json()["error"]


def test_simulate_rejects_non_numeric_arrival_rate():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 5, "arrival_rate": "rapido", "seed": 7}
    )

    assert response.status_code == 400
    assert "arrival_rate" in response.get_json()["error"]


def test_simulate_treats_null_arrival_rate_as_absent():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 20, "arrival_rate": None, "seed": 11}
    )

    expected = CasillaModel(num_voters=20, arrival_rate=1 / 3, rng=11)
    expected.run_to_completion()
    assert response.status_code == 200
    assert response.get_json()["summary"]["duration_minutes"] == pytest.approx(expected.time)


def test_simulate_forces_the_requested_external_event():
    client = app.test_client()

    response = client.post(
        "/simulate",
        json={
            "num_voters": 10,
            "seed": 7,
            "forced_event_kind": "aguacero",
            "forced_event_time": 5.0,
            "forced_event_duration": 20.0,
        },
    )

    assert response.status_code == 200
    external = response.get_json()["external_events"]
    assert len(external) == 1
    assert external[0]["kind"] == "aguacero"
    assert external[0]["t_start"] == pytest.approx(5.0)
    assert external[0]["duration"] == pytest.approx(20.0)


def test_simulate_rejects_unknown_external_event_kind():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 5, "seed": 7, "forced_event_kind": "tormenta"}
    )

    assert response.status_code == 400
    assert "forced_event_kind" in response.get_json()["error"]


def test_simulate_rejects_zero_forced_event_time():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 5, "seed": 7, "forced_event_time": 0}
    )

    assert response.status_code == 400
    assert "forced_event_time" in response.get_json()["error"]


def test_simulate_rejects_non_numeric_forced_event_duration():
    client = app.test_client()

    response = client.post(
        "/simulate", json={"num_voters": 5, "seed": 7, "forced_event_duration": "mucho"}
    )

    assert response.status_code == 400
    assert "forced_event_duration" in response.get_json()["error"]


def test_simulate_treats_null_forced_event_fields_as_absent():
    # This is exactly what Unity sends while the dropdown reads "Aleatorio".
    client = app.test_client()

    response = client.post(
        "/simulate",
        json={
            "num_voters": 20,
            "seed": 11,
            "forced_event_kind": None,
            "forced_event_time": None,
            "forced_event_duration": None,
        },
    )

    expected = CasillaModel(num_voters=20, rng=11)
    expected.run_to_completion()
    assert response.status_code == 200
    assert response.get_json()["summary"]["duration_minutes"] == pytest.approx(
        expected.time
    )
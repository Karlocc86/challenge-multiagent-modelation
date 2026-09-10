import pytest

from casilla import CasillaModel
from server import app


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
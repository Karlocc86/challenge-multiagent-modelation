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

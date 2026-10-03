def test_health(client):
    r = client.get("/health")
    assert r.status_code == 200 and r.json()["status"] == "ok"


def test_rules_info(client):
    r = client.get("/rules/eskom/0.1.0")
    assert r.status_code == 200 and r.json()["ref"] == "eskom/0.1.0"
    assert client.get("/rules/eskom/0.0.0").status_code == 404


def test_voltage_drop_traced(client):
    r = client.post(
        "/calc/lv/voltage-drop",
        json={"rules": "eskom/0.1.0", "conductor": "ABC-70", "current_a": 100, "length_km": 0.5, "power_factor": 0.9},
    )
    assert r.status_code == 200
    body = r.json()
    assert body["passes"] is False
    t = body["drop_pct"]
    assert t["formula_id"] and t["clause"] and t["rules_hash"] == body["rules_hash"]
    assert {i["name"] for i in t["inputs"]} == {"I", "L", "cos_phi", "R", "X", "V_nominal"}

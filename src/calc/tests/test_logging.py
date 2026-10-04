import json
import logging

from reticula_calc.logging_setup import JsonFormatter, trace_id


def test_trace_id_from_traceparent():
    assert trace_id("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01") == "4bf92f3577b34da6a3ce929d0e0e4736"
    assert trace_id(None) is None
    assert trace_id("garbage") is None


def test_request_is_logged_as_json_with_trace_id(client, caplog):
    caplog.set_level(logging.INFO, logger="reticula_calc")
    client.get("/health", headers={"traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"})
    record = next(r for r in caplog.records if r.getMessage() == "request")
    line = json.loads(JsonFormatter().format(record))
    assert line["path"] == "/health"
    assert line["status"] == 200
    assert line["trace_id"] == "4bf92f3577b34da6a3ce929d0e0e4736"
    assert line["level"] == "info"

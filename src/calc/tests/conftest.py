import pytest
from fastapi.testclient import TestClient

from reticula_calc.app import app


@pytest.fixture
def client() -> TestClient:
    return TestClient(app)

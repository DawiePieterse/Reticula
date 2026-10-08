"""The one shape for a problem a calc reports: a severity, a code, a message, and a capped sample of where to look."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel

MAX_SAMPLES = 10
Severity = Literal["error", "warning"]


class Issue(BaseModel):
    severity: Severity
    code: str
    message: str
    count: int = 1
    samples: list[str] = []
    at: list[tuple[float, float]] = []
    """Where to look, as lon/lat."""


def issue(severity: Severity, code: str, message: str, samples: list[str] = (), at: list[tuple[float, float]] = ()) -> Issue:
    """An issue that counts every item and keeps the first few as samples."""
    samples, at = list(samples), list(at)
    return Issue(severity=severity, code=code, message=message, count=max(len(samples), len(at), 1),
                 samples=samples[:MAX_SAMPLES], at=at[:MAX_SAMPLES])

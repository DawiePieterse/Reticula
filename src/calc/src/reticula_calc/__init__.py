"""Reticula calculation service. Every engineering number is computed here and nowhere else."""

__version__ = "0.1.0"


def main() -> None:
    import uvicorn

    uvicorn.run("reticula_calc.app:app", host="0.0.0.0", port=8001)

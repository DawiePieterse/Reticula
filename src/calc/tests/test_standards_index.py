"""The standards clause index (docs/standards-index.md) and the rules files stay in step."""

import re
from pathlib import Path

import yaml

from reticula_calc.rules import list_rules
from reticula_calc.rules.loader import rules_dir

INDEX = Path(__file__).resolve().parents[3] / "docs" / "standards-index.md"
ID = re.compile(r"^[A-Z0-9]+(-[A-Z0-9]+)+$")


def index_ids() -> list[str]:
    ids = []
    for line in INDEX.read_text(encoding="utf-8").splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if line.startswith("|") and cells and ID.match(cells[0]):
            ids.append(cells[0])
    return ids


def indexes_in(node) -> list[str]:
    if isinstance(node, dict):
        found = [node["index"]] if isinstance(node.get("index"), str) else []
        return found + [i for v in node.values() for i in indexes_in(v)]
    if isinstance(node, list):
        return [i for v in node for i in indexes_in(v)]
    return []


def rules_docs():
    for ref in list_rules():
        authority, version = ref.split("/")
        yield ref, yaml.safe_load((rules_dir() / authority / f"{version}.yaml").read_text(encoding="utf-8"))


def test_ids_are_unique_and_well_formed():
    ids = index_ids()
    assert len(ids) >= 20
    assert len(ids) == len(set(ids)), [i for i in ids if ids.count(i) > 1]


def test_every_index_in_a_rules_file_exists():
    known = set(index_ids())
    for ref, doc in rules_docs():
        unknown = set(indexes_in(doc)) - known
        assert not unknown, f"{ref} points at index ids that docs/standards-index.md lacks: {sorted(unknown)}"


def test_every_standard_a_rules_file_names_is_indexed():
    prefixes = {i.split("-")[0] for i in index_ids()}
    for ref, doc in rules_docs():
        for standard in doc.get("standards", {}):
            # Eskom documents are numbered (ESKOM240-56030637); their rows share the ESKOM prefix.
            stem = "ESKOM" if standard.startswith("ESKOM") else re.match(r"[A-Z]+\d+", standard.replace(" ", "")).group()
            assert stem in prefixes, f"{ref} names {standard}, which has no entry in docs/standards-index.md"


def test_rules_files_may_carry_index_ids_beside_clauses():
    import jsonschema
    import pytest

    from reticula_calc.rules.loader import _schema

    doc = next(d for ref, d in rules_docs() if ref == "eskom/0.2.0")
    doc["effective_date"] = str(doc["effective_date"])
    doc["voltage"]["index"] = "NRS048-VLIMIT"
    jsonschema.validate(doc, _schema())
    doc["voltage"]["index"] = "not an id"
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(doc, _schema())

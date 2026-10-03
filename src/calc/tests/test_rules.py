import pytest

from reticula_calc.rules import RulesError, list_rules, load_rules


def test_lists_eskom_starter():
    assert "eskom/0.1.0" in list_rules()


def test_load_hash_is_stable():
    a = load_rules("eskom/0.1.0")
    b = load_rules("eskom/0.1.0")
    assert a.hash == b.hash and len(a.hash) == 16
    assert a.conductor("ABC-70").rating_a == 200


def test_unknown_rules():
    with pytest.raises(RulesError):
        load_rules("nowhere/9.9.9")


def test_unknown_conductor():
    with pytest.raises(RulesError):
        load_rules("eskom/0.1.0").conductor("NOPE")

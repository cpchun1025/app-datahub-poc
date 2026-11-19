import importlib.util
import sys
from pathlib import Path

import httpx
import pytest

from datahub_client import DataHubClient
from test_client import NOW, news_item, news_page, page, price, source


def load_example(name):
    path = Path(__file__).parents[1] / "examples" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_demo_reports_bounded_scan(monkeypatch, capsys):
    demo = load_example("demo")

    def handler(request):
        if request.url.path == "/api/sources":
            return httpx.Response(200, json={"serverTimeUtc": NOW, "sources": [source()]})
        if request.url.path == "/api/news":
            return httpx.Response(200, json=news_page([news_item()]))
        number = int(request.url.params["page"])
        return httpx.Response(200, json=page([price()], number=number, total_pages=5))

    monkeypatch.setattr(demo, "DataHubClient",
                        lambda url, timeout: DataHubClient(url, timeout=timeout, transport=httpx.MockTransport(handler)))
    monkeypatch.setattr(sys, "argv", ["demo", "--base-url", "http://datahub.test", "--max-pages", "1"])
    assert demo.main() == 0
    output = capsys.readouterr().out
    assert "Recovering" in output and "precision=Date" in output
    assert "Page limit reached; results may be incomplete" in output


def test_demo_uses_api_for_selected_instrument_and_memberships(monkeypatch, capsys):
    demo = load_example("demo")

    def handler(request):
        if request.url.path == "/api/sources":
            return httpx.Response(200, json={"serverTimeUtc": NOW, "sources": [source()]})
        if request.url.path == "/api/news":
            return httpx.Response(200, json=news_page([]))
        if request.url.path == "/api/instruments/hk-0005/memberships":
            from test_client import memberships
            return httpx.Response(200, json=memberships())
        if request.url.path == "/api/instruments/hk-0005":
            from test_client import instrument_detail
            return httpx.Response(200, json=instrument_detail())
        return httpx.Response(200, json=page([]))

    monkeypatch.setattr(demo, "DataHubClient",
                        lambda url, timeout: DataHubClient(url, timeout=timeout, transport=httpx.MockTransport(handler)))
    monkeypatch.setattr(sys, "argv", ["demo", "--base-url", "http://datahub.test", "--instrument-id", "hk-0005"])
    assert demo.main() == 0
    output = capsys.readouterr().out
    assert "Instrument hk-0005 source observations" in output
    assert "0005.HK" in output
    assert "DEMO-HK-A" in output


def test_demo_failure_is_nonzero_and_readable(monkeypatch, capsys):
    demo = load_example("demo")
    monkeypatch.setattr(sys, "argv", ["demo", "--base-url", "http://[invalid"])
    assert demo.main() == 1
    assert "Data Hub error" in capsys.readouterr().err


@pytest.mark.parametrize("url", ["http://example.test:5080", "http://127.0.0.1",
                                "http://127.0.0.1:5080/prefix", "https://localhost:5080"])
def test_smoke_refuses_non_localhost_or_ambiguous_urls(monkeypatch, capsys, url):
    smoke = load_example("smoke")
    monkeypatch.setattr(sys, "argv", ["smoke", "--base-url", url])
    monkeypatch.setattr(smoke.httpx, "Client", lambda *args, **kwargs: pytest.fail("No network request expected"))
    assert smoke.main() == 1
    assert "Smoke test failed" in capsys.readouterr().err


def test_smoke_refuses_backend_without_mock_mode(monkeypatch, capsys):
    smoke = load_example("smoke")
    original_client = httpx.Client
    monkeypatch.setattr(sys, "argv", ["smoke", "--base-url", "http://127.0.0.1:5080"])
    monkeypatch.setattr(smoke.httpx, "Client", lambda *args, **kwargs: original_client(
        *args, transport=httpx.MockTransport(lambda request: httpx.Response(200, json={"mockMode": False})), **kwargs))
    assert smoke.main() == 1
    assert "mockMode=true" in capsys.readouterr().err


def test_csv_generator_scenarios_are_deterministic_and_atomic(tmp_path):
    generator = load_example("generate_csv_showcase")
    initial = generator.datasets(17, "initial")
    repeated = generator.datasets(17, "initial")
    following = generator.datasets(17, "next")
    malformed = generator.datasets(17, "malformed")
    assert initial == repeated
    initial_spot = {row[0] for row in initial["spot.csv"][1:]}
    next_spot = {row[0] for row in following["spot.csv"][1:]}
    assert len(initial_spot) == 20
    assert next_spot - initial_spot == {"hk-0025"}
    assert initial_spot - next_spot == {"hk-0024"}
    assert any("not-a-decimal" == row[2] for row in malformed["spot.csv"][1:])
    initial_members = {(row[0], row[1]): row[2] for row in initial["composition.csv"][1:]}
    next_members = {(row[0], row[1]): row[2] for row in following["composition.csv"][1:]}
    assert ("DEMO-HK-LARGE", "hk-0005") in next_members
    assert initial_members[("DEMO-HK-LARGE", "hk-0005")] != next_members[("DEMO-HK-LARGE", "hk-0005")]
    initial_large = {instrument for index, instrument in initial_members if index == "DEMO-HK-LARGE"}
    initial_growth = {instrument for index, instrument in initial_members if index == "DEMO-HK-GROWTH"}
    assert initial_large & initial_growth
    assert "composition.csv" not in malformed

    generator.publish(tmp_path / "spot.csv", initial["spot.csv"])
    generator.publish(tmp_path / "composition.csv", initial["composition.csv"])
    prior_composition = (tmp_path / "composition.csv").read_bytes()
    generator.publish(tmp_path / "spot.csv", malformed["spot.csv"])
    assert (tmp_path / "composition.csv").read_bytes() == prior_composition
    generator.publish(tmp_path / "spot.csv", following["spot.csv"])
    assert (tmp_path / "spot.csv").read_text(encoding="utf-8").count("\n") == len(following["spot.csv"])
    assert list(tmp_path.glob(".*.tmp")) == []

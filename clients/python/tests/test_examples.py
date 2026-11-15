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

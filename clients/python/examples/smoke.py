import argparse
import math
import sys
from urllib.parse import urlsplit

import httpx

from datahub_client import DataHubClient, DataHubError


def main() -> int:
    parser = argparse.ArgumentParser(description="Opt-in read-only localhost mock backend smoke test.")
    parser.add_argument("--base-url", required=True, help="Explicit localhost backend URL, including port")
    parser.add_argument("--timeout", type=float, default=5.0)
    args = parser.parse_args()
    try:
        url = urlsplit(args.base_url)
        if (url.scheme != "http" or url.hostname not in ("localhost", "127.0.0.1", "::1")
                or url.port is None or url.username is not None or url.password is not None
                or url.path not in ("", "/") or url.query or url.fragment):
            raise ValueError("base URL must be an explicit HTTP localhost URL with port and no path")
        if not math.isfinite(args.timeout) or args.timeout <= 0:
            raise ValueError("timeout must be positive and finite")
        with httpx.Client(base_url=args.base_url, timeout=args.timeout, trust_env=False,
                          follow_redirects=False) as http:
            response = http.get("/api/health")
            response.raise_for_status()
            health = response.json()
            if not isinstance(health, dict) or health.get("mockMode") is not True:
                raise ValueError("backend did not report mockMode=true")
        with DataHubClient(args.base_url, timeout=args.timeout) as client:
            sources = client.sources()["sources"]
            if not sources or not all(source.get("isMock") is True for source in sources):
                raise ValueError("backend did not report exclusively mock price sources")
            prices = client.prices(page_size=2)
            news = client.news(page_size=2)
            if news["source"]["sourceId"] != "mock-news":
                raise ValueError("backend did not report mock-news")
            print(f"Mock backend OK: {len(sources)} sources, {len(prices['items'])} prices, "
                  f"{len(news['items'])} news items. Initializing/empty is possible.")
    except (DataHubError, httpx.HTTPError, httpx.InvalidURL, ValueError) as exc:
        print(f"Smoke test failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

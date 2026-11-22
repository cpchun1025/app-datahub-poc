import json
from decimal import Decimal

import httpx
import pytest

from datahub_client import ApiError, ConnectionError, ContractError, DataHubClient, TimeoutError


NOW = "2026-01-02T03:04:05Z"


def price(price_value=10):
    return {
        "sourceId": "mock-a", "instrumentId": "ABC", "price": price_value, "currency": "USD",
        "sourceUpdatedAtUtc": NOW, "receivedAtUtc": NOW, "sourceRevision": 3,
        "sourceStatus": "Recovering",
    }


def freshness():
    return {"sourceId": "mock-a", "status": "Stale", "syncedSinceStart": True,
            "lastUpdateReceivedAtUtc": None}


def page(items, *, number=1, total_pages=1):
    return {"serverTimeUtc": NOW, "page": number, "pageSize": 2, "totalCount": len(items),
            "totalPages": total_pages, "items": items, "sources": [freshness()]}


def source():
    return {"sourceId": "mock-a", "status": "Recovering", "recordCount": 1,
            "syncedSinceStart": False, "restoredFromCheckpoint": True,
            "lastUpdateReceivedAtUtc": None, "isMock": True, "lastError": None}


def news_item():
    return {"sourceId": "mock-news", "articleId": "n1", "title": "Synthetic", "url": "https://example.test/",
            "publisher": "Mock", "originalPublisher": None, "category": None, "publishedAtUtc": None,
            "publishedDate": "2026-01-02", "publicationTimePrecision": "Date",
            "originalPublicationTime": "2026-01-02", "firstSeenAtUtc": NOW, "fetchedAtUtc": NOW,
            "language": "en", "mappingWarnings": ["timezone unknown"]}


def news_source():
    return {"sourceId": "mock-news", "state": "Recovering", "lastAttemptAtUtc": None,
            "lastSuccessfulFetchAtUtc": None, "latestArticlePublishedAtUtc": None,
            "lastError": None, "cachedItemCount": 1}


def instrument_observation():
    return {
        "sourceId": "demo-csv", "displayName": "Synthetic CSV", "instrumentId": "hk-0005",
        "providerSymbol": "0005.HK", "price": 1, "currency": "HKD",
        "sourceUpdatedAtUtc": NOW, "receivedAtUtc": NOW, "priceKind": "Last",
        "sourceStatus": "Live", "isSynthetic": True,
    }


def instrument_detail():
    return {
        "instrumentId": "hk-0005", "serverTimeUtc": NOW, "page": 1, "pageSize": 2,
        "totalCount": 1, "totalPages": 1, "observations": [instrument_observation()],
    }


def memberships():
    return {
        "instrumentId": "hk-0005", "serverTimeUtc": NOW, "sourceId": "demo-composition",
        "sourceStatus": "Connected", "lastSuccessfulValidationAtUtc": NOW, "lastError": None,
        "page": 1, "pageSize": 2, "totalCount": 1, "totalPages": 1,
        "items": [{"sourceId": "demo-composition", "indexId": "DEMO-HK-A",
                   "instrumentId": "hk-0005", "weight": 0.25, "effectiveDate": "2026-03-01"}],
    }


def news_page(items):
    result = page(items)
    del result["sources"]
    result["source"] = news_source()
    return result


def client(handler):
    return DataHubClient("http://datahub.test", transport=httpx.MockTransport(handler))


def test_prices_decimal_tokens_preserve_precision_and_integer_fields():
    body = json.dumps(page([price()])).replace('"price": 10', '"price": 12345678901234567890.12345678')
    with client(lambda request: httpx.Response(200, content=body)) as hub:
        result = hub.prices(page_size=2)
    assert result["items"][0]["price"] == Decimal("12345678901234567890.12345678")
    assert str(result["items"][0]["price"]) == "12345678901234567890.12345678"
    assert result["items"][0]["sourceStatus"] == "Recovering"
    assert result["sources"][0]["status"] == "Stale"
    assert type(result["totalCount"]) is int
    assert type(result["items"][0]["sourceRevision"]) is int


def test_integer_decimal_token_and_detail_url_encoding():
    requests = []

    def handler(request):
        requests.append(request)
        return httpx.Response(200, json=price(117))

    with client(handler) as hub:
        item = hub.price("mock-a", "ABC & XYZ")
    assert item["price"] == Decimal("117")
    assert type(item["price"]) is Decimal
    assert requests[0].url.raw_path == b"/api/prices/mock-a/ABC%20%26%20XYZ"


def test_instrument_detail_and_memberships_preserve_decimal_and_pagination():
    requests = []

    def handler(request):
        requests.append(request)
        if request.url.path.endswith("/memberships"):
            body = json.dumps(memberships()).replace('"weight": 0.25', '"weight": 0.1234567890123456789012345678')
        else:
            body = json.dumps(instrument_detail()).replace('"price": 1', '"price": 12345678901234567890.12345678')
        return httpx.Response(200, content=body)

    with client(handler) as hub:
        detail = hub.instrument("hk-0005", page=2, page_size=2)
        related = hub.memberships("hk-0005", page=3, page_size=2)

    assert detail["observations"][0]["price"] == Decimal("12345678901234567890.12345678")
    assert detail["observations"][0]["providerSymbol"] == "0005.HK"
    assert related["items"][0]["weight"] == Decimal("0.1234567890123456789012345678")
    assert requests[0].url.path == "/api/instruments/hk-0005"
    assert dict(requests[0].url.params) == {"page": "2", "pageSize": "2"}
    assert requests[1].url.path == "/api/instruments/hk-0005/memberships"


def test_instrument_detail_rejects_bad_contract_and_unaddressable_path_segment():
    with client(lambda request: httpx.Response(200, json={})) as hub:
        with pytest.raises(ContractError, match="instrumentId"):
            hub.instrument("hk-0005")
        with pytest.raises(ValueError):
            hub.memberships("hk/0005")


def test_query_filters_and_sources():
    requests = []

    def handler(request):
        requests.append(request)
        if request.url.path == "/api/sources":
            return httpx.Response(200, json={"serverTimeUtc": NOW, "sources": [source()]})
        if request.url.path == "/api/news":
            return httpx.Response(200, json=news_page([news_item()]))
        return httpx.Response(200, json=page([price()]))

    with client(handler) as hub:
        assert hub.sources()["sources"][0]["status"] == "Recovering"
        hub.prices(page=2, page_size=2, source_id="mock-a", instrument_id="A B", sort_by="price",
                   sort_direction="desc", currency="USD", source_status="Stale",
                   min_price=Decimal("1E-8"), max_price="123.40")
        hub.news(page=3, page_size=2, keyword="a & b", category="Markets",
                 published_from="2026-01-01", published_to="2026-01-02", sort_direction="asc")
    assert dict(requests[1].url.params) == {
        "page": "2", "pageSize": "2", "sourceId": "mock-a", "instrumentId": "A B", "sortBy": "price",
        "sortDirection": "desc", "currency": "USD", "sourceStatus": "Stale",
        "minPrice": "0.00000001", "maxPrice": "123.40",
    }
    assert dict(requests[2].url.params) == {
        "page": "3", "pageSize": "2", "keyword": "a & b", "category": "Markets",
        "publishedFrom": "2026-01-01", "publishedTo": "2026-01-02", "sortDirection": "asc",
    }


def test_news_date_only_and_unknown_are_not_coerced():
    unknown = {**news_item(), "articleId": "n2", "publishedDate": None,
               "publicationTimePrecision": "Unknown", "originalPublicationTime": "unknown zone"}
    with client(lambda request: httpx.Response(200, json=news_page([news_item(), unknown]))) as hub:
        result = hub.news(page_size=2)
    first, second = result["items"]
    assert first["publishedDate"] == "2026-01-02" and first["publishedAtUtc"] is None
    assert first["publicationTimePrecision"] == "Date"
    assert second["publishedDate"] is None and second["publishedAtUtc"] is None
    assert second["originalPublicationTime"] == "unknown zone"
    assert first["fetchedAtUtc"] == NOW and first["mappingWarnings"] == ["timezone unknown"]
    assert result["source"]["state"] == "Recovering"
    assert result["source"]["lastSuccessfulFetchAtUtc"] is None


def test_empty_results_are_not_errors():
    def handler(request):
        return httpx.Response(200, json=news_page([]) if request.url.path.endswith("news")
                              else page([], total_pages=0))

    with client(handler) as hub:
        assert hub.prices()["items"] == []
        assert hub.news()["items"] == []
        assert not hub.price_pages(max_pages=2).may_have_more


@pytest.mark.parametrize(("total_pages", "max_pages", "requested", "truncated"),
                         [(4, 2, [1, 2], True), (2, 4, [1, 2], False)])
def test_bounded_pages(total_pages, max_pages, requested, truncated):
    seen = []

    def handler(request):
        number = int(request.url.params["page"])
        seen.append(number)
        return httpx.Response(200, json=page([price()], number=number, total_pages=total_pages))

    with client(handler) as hub:
        result = hub.price_pages(max_pages=max_pages, page_size=2)
    assert seen == requested
    assert len(result.pages) == len(requested)
    assert result.may_have_more is truncated


@pytest.mark.parametrize("limit", [0, -1, True, 1.5])
def test_page_limit_must_be_positive_integer(limit):
    with client(lambda request: pytest.fail("No request expected")) as hub:
        with pytest.raises(ValueError):
            hub.price_pages(max_pages=limit)


def test_wrong_page_number_is_a_contract_error():
    with client(lambda request: httpx.Response(200, json=page([], number=2))) as hub:
        with pytest.raises(ContractError, match="does not match"):
            hub.price_pages(max_pages=1)


def test_problem_details_and_non_json_error():
    def handler(request):
        if request.url.path.endswith("news"):
            return httpx.Response(502, text="<html>bad gateway</html>")
        return httpx.Response(400, json={"title": "Validation failed", "detail": "Bad page",
                                         "errors": {"page": ["Invalid"]}})

    with client(handler) as hub:
        with pytest.raises(ApiError) as problem:
            hub.prices()
        assert problem.value.status == 400
        assert problem.value.errors == {"page": ["Invalid"]}
        assert "Bad page" in str(problem.value)
        with pytest.raises(ApiError) as gateway:
            hub.news()
        assert gateway.value.status == 502
        assert "bad gateway" in str(gateway.value)


@pytest.mark.parametrize(("error", "expected"), [
    (httpx.ReadTimeout("too slow"), TimeoutError),
    (httpx.ConnectError("unreachable"), ConnectionError),
])
def test_transport_errors_are_distinct(error, expected):
    def handler(request):
        raise error

    with client(handler) as hub:
        with pytest.raises(expected):
            hub.sources()


@pytest.mark.parametrize("body", [b"{", b'{"items":NaN}', b"[]", b'{"serverTimeUtc":null,"sources":[]}'])
def test_invalid_json_or_required_contract_is_explicit(body):
    with client(lambda request: httpx.Response(200, content=body)) as hub:
        with pytest.raises(ContractError):
            hub.sources()


@pytest.mark.parametrize("bad_price", [None, "12.5", True])
def test_invalid_decimal_wire_values_are_rejected(bad_price):
    with client(lambda request: httpx.Response(200, json=page([price(bad_price)]))) as hub:
        with pytest.raises(ContractError, match="price"):
            hub.prices()


def test_float_price_filter_is_not_silently_rounded():
    with client(lambda request: pytest.fail("No request expected")) as hub:
        with pytest.raises(TypeError):
            hub.prices(min_price=0.1)


@pytest.mark.parametrize("timeout", [0, -1, float("inf"), float("nan")])
def test_timeout_must_be_finite(timeout):
    with pytest.raises(ValueError):
        DataHubClient("http://datahub.test", timeout=timeout)


def test_context_manager_closes_transport():
    transport = httpx.MockTransport(lambda request: httpx.Response(200))
    with DataHubClient("http://datahub.test", transport=transport) as hub:
        assert not hub._client.is_closed
    assert hub._client.is_closed


def test_invalid_base_url_has_clear_error():
    with pytest.raises(ValueError, match="base_url"):
        DataHubClient("http://[invalid")

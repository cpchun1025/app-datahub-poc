from __future__ import annotations

import json
import math
from dataclasses import dataclass
from decimal import Decimal
from typing import Any
from urllib.parse import quote, urlsplit

import httpx


class DataHubError(Exception):
    """Base class for failures contacting or interpreting the API."""


class TimeoutError(DataHubError):
    pass


class ConnectionError(DataHubError):
    pass


class ContractError(DataHubError):
    pass


class ApiError(DataHubError):
    def __init__(self, status: int, title: str, detail: str, errors: Any = None):
        self.status = status
        self.title = title
        self.detail = detail
        self.errors = errors
        super().__init__(f"HTTP {status}: {title}" + (f" — {detail}" if detail else ""))


@dataclass(frozen=True)
class PageBatch:
    pages: tuple[dict[str, Any], ...]
    may_have_more: bool


def _reject_constant(value: str) -> None:
    raise ValueError(f"Invalid JSON number: {value}")


def _json(content: bytes) -> Any:
    return json.loads(content, parse_float=Decimal, parse_constant=_reject_constant)


def _object(value: Any, where: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ContractError(f"{where} must be an object")
    return value


def _field(obj: dict[str, Any], name: str, expected: type, *, nullable: bool = False) -> Any:
    if name not in obj:
        raise ContractError(f"Missing required field: {name}")
    value = obj[name]
    if nullable and value is None:
        return value
    if not isinstance(value, expected) or (expected is int and isinstance(value, bool)):
        raise ContractError(f"Invalid field: {name}")
    return value


def _price(value: Any) -> dict[str, Any]:
    item = _object(value, "Price")
    for name in ("sourceId", "instrumentId", "currency", "sourceUpdatedAtUtc", "receivedAtUtc", "sourceStatus"):
        _field(item, name, str)
    _field(item, "sourceRevision", int)
    price = item.get("price")
    if isinstance(price, bool) or not isinstance(price, (int, Decimal)):
        raise ContractError("Invalid or missing field: price")
    item["price"] = Decimal(price)
    return item


def _news(value: Any) -> dict[str, Any]:
    item = _object(value, "News item")
    for name in ("sourceId", "articleId", "title", "url", "publisher", "publicationTimePrecision",
                 "firstSeenAtUtc", "fetchedAtUtc", "language"):
        _field(item, name, str)
    for name in ("originalPublisher", "category", "publishedAtUtc", "publishedDate", "originalPublicationTime"):
        _field(item, name, str, nullable=True)
    warnings = _field(item, "mappingWarnings", list)
    if not all(isinstance(warning, str) for warning in warnings):
        raise ContractError("Invalid field: mappingWarnings")
    return item


def _source(value: Any) -> dict[str, Any]:
    source = _object(value, "Source")
    _field(source, "sourceId", str)
    _field(source, "status", str)
    _field(source, "recordCount", int)
    _field(source, "syncedSinceStart", bool)
    _field(source, "restoredFromCheckpoint", bool)
    _field(source, "lastUpdateReceivedAtUtc", str, nullable=True)
    _field(source, "lastError", str, nullable=True)
    return source


def _news_source(value: Any) -> dict[str, Any]:
    source = _object(value, "News source")
    for name in ("sourceId", "state"):
        _field(source, name, str)
    for name in ("lastAttemptAtUtc", "lastSuccessfulFetchAtUtc", "latestArticlePublishedAtUtc", "lastError"):
        _field(source, name, str, nullable=True)
    _field(source, "cachedItemCount", int)
    return source


def _page(value: Any, item_parser: Any) -> dict[str, Any]:
    page = _object(value, "Page")
    _field(page, "serverTimeUtc", str)
    for name in ("page", "pageSize", "totalCount", "totalPages"):
        number = _field(page, name, int)
        if number < (1 if name in ("page", "pageSize") else 0):
            raise ContractError(f"Invalid field: {name}")
    page["items"] = [item_parser(item) for item in _field(page, "items", list)]
    return page


def _instrument_observation(value: Any) -> dict[str, Any]:
    item = _object(value, "Instrument observation")
    for name in ("sourceId", "displayName", "instrumentId", "currency", "sourceUpdatedAtUtc",
                 "receivedAtUtc", "sourceStatus"):
        _field(item, name, str)
    _field(item, "providerSymbol", str, nullable=True)
    _field(item, "priceKind", str, nullable=True)
    _field(item, "isSynthetic", bool)
    price = item.get("price")
    if isinstance(price, bool) or not isinstance(price, (int, Decimal)):
        raise ContractError("Invalid or missing field: price")
    item["price"] = Decimal(price)
    return item


def _membership(value: Any) -> dict[str, Any]:
    item = _object(value, "Index membership")
    for name in ("sourceId", "indexId", "instrumentId", "effectiveDate"):
        _field(item, name, str)
    weight = item.get("weight")
    if isinstance(weight, bool) or not isinstance(weight, (int, Decimal)):
        raise ContractError("Invalid or missing field: weight")
    item["weight"] = Decimal(weight)
    return item


class DataHubClient:
    def __init__(self, base_url: str, *, timeout: float = 5.0, transport: httpx.BaseTransport | None = None):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be a positive finite number of seconds")
        try:
            parts = urlsplit(base_url)
            _ = parts.port
            url = httpx.URL(base_url)
        except (httpx.InvalidURL, ValueError) as exc:
            raise ValueError(f"Invalid base_url: {exc}") from exc
        if url.scheme not in ("http", "https") or not parts.hostname or url.query or url.fragment:
            raise ValueError("base_url must be an absolute HTTP(S) URL without query or fragment")
        self._client = httpx.Client(base_url=base_url.rstrip("/") + "/", timeout=timeout,
                                    transport=transport, follow_redirects=False, trust_env=False)

    def __enter__(self) -> DataHubClient:
        return self

    def __exit__(self, *args: Any) -> None:
        self.close()

    def close(self) -> None:
        self._client.close()

    def _get(self, path: str, params: dict[str, Any] | None = None) -> Any:
        try:
            response = self._client.get(path, params=params)
        except httpx.TimeoutException as exc:
            raise TimeoutError(f"Request timed out: {path}") from exc
        except httpx.RequestError as exc:
            raise ConnectionError(f"Request failed: {path}: {exc}") from exc
        if not response.is_success:
            title = response.reason_phrase or "HTTP error"
            detail = ""
            errors = None
            try:
                body = _json(response.content)
            except (ValueError, UnicodeDecodeError):
                body = None
            if isinstance(body, dict):
                if isinstance(body.get("title"), str):
                    title = body["title"]
                if isinstance(body.get("detail"), str):
                    detail = body["detail"]
                errors = body.get("errors")
            elif response.content:
                detail = response.text[:500]
            raise ApiError(response.status_code, title, detail, errors)
        try:
            return _json(response.content)
        except (ValueError, UnicodeDecodeError) as exc:
            raise ContractError(f"Invalid JSON response: {path}") from exc

    def sources(self) -> dict[str, Any]:
        data = _object(self._get("api/sources"), "Sources response")
        _field(data, "serverTimeUtc", str)
        data["sources"] = [_source(item) for item in _field(data, "sources", list)]
        return data

    def prices(self, *, page: int = 1, page_size: int = 100, source_id: str | None = None,
               instrument_id: str | None = None, sort_by: str | None = None,
               sort_direction: str | None = None, currency: str | None = None,
               source_status: str | None = None, min_price: Decimal | str | None = None,
               max_price: Decimal | str | None = None) -> dict[str, Any]:
        params = _params(page=page, pageSize=page_size, sourceId=source_id, instrumentId=instrument_id,
                         sortBy=sort_by, sortDirection=sort_direction, currency=currency,
                         sourceStatus=source_status, minPrice=_decimal_param(min_price),
                         maxPrice=_decimal_param(max_price))
        data = _page(self._get("api/prices", params), _price)
        for source in _field(data, "sources", list):
            source = _object(source, "Price source freshness")
            _field(source, "sourceId", str)
            _field(source, "status", str)
            _field(source, "syncedSinceStart", bool)
            _field(source, "lastUpdateReceivedAtUtc", str, nullable=True)
        return data

    def price(self, source_id: str, instrument_id: str) -> dict[str, Any]:
        if any(not value or value in (".", "..") or "/" in value or "\\" in value
               for value in (source_id, instrument_id)):
            raise ValueError("source_id and instrument_id must be nonempty path segments")
        return _price(self._get(f"api/prices/{quote(source_id, safe='')}/{quote(instrument_id, safe='')}"))

    def instrument(self, instrument_id: str, *, page: int = 1, page_size: int = 100) -> dict[str, Any]:
        _path_segment(instrument_id, "instrument_id")
        data = _object(self._get(
            f"api/instruments/{quote(instrument_id, safe='')}",
            _params(page=page, pageSize=page_size),
        ), "Instrument detail")
        _field(data, "instrumentId", str)
        _field(data, "serverTimeUtc", str)
        _page_metadata(data)
        data["observations"] = [_instrument_observation(item)
                                for item in _field(data, "observations", list)]
        return data

    def memberships(self, instrument_id: str, *, page: int = 1, page_size: int = 100) -> dict[str, Any]:
        _path_segment(instrument_id, "instrument_id")
        data = _object(self._get(
            f"api/instruments/{quote(instrument_id, safe='')}/memberships",
            _params(page=page, pageSize=page_size),
        ), "Instrument memberships")
        _field(data, "instrumentId", str)
        _field(data, "serverTimeUtc", str)
        _field(data, "sourceId", str)
        _field(data, "sourceStatus", str)
        _field(data, "lastSuccessfulValidationAtUtc", str, nullable=True)
        _field(data, "lastError", str, nullable=True)
        _page_metadata(data)
        data["items"] = [_membership(item) for item in _field(data, "items", list)]
        return data

    def news(self, *, page: int = 1, page_size: int = 100, keyword: str | None = None,
             category: str | None = None, published_from: str | None = None,
             published_to: str | None = None, sort_direction: str | None = None) -> dict[str, Any]:
        params = _params(page=page, pageSize=page_size, keyword=keyword, category=category,
                         publishedFrom=published_from, publishedTo=published_to, sortDirection=sort_direction)
        data = _page(self._get("api/news", params), _news)
        _news_source(data.get("source"))
        return data

    def price_pages(self, *, max_pages: int, page_size: int = 100, **filters: Any) -> PageBatch:
        if isinstance(max_pages, bool) or not isinstance(max_pages, int) or max_pages < 1:
            raise ValueError("max_pages must be a positive integer")
        pages = []
        for page_number in range(1, max_pages + 1):
            page = self.prices(page=page_number, page_size=page_size, **filters)
            if page["page"] != page_number:
                raise ContractError("Response page does not match requested page")
            pages.append(page)
            if page_number >= page["totalPages"]:
                return PageBatch(tuple(pages), False)
        return PageBatch(tuple(pages), True)


def _params(**values: Any) -> dict[str, Any]:
    return {key: value for key, value in values.items() if value is not None}


def _path_segment(value: str, name: str) -> None:
    if not isinstance(value, str) or not value or value in (".", "..") or "/" in value or "\\" in value:
        raise ValueError(f"{name} must be a nonempty path segment")


def _page_metadata(page: dict[str, Any]) -> None:
    for name in ("page", "pageSize", "totalCount", "totalPages"):
        number = _field(page, name, int)
        if number < (1 if name in ("page", "pageSize") else 0):
            raise ContractError(f"Invalid field: {name}")


def _decimal_param(value: Decimal | str | None) -> str | None:
    if value is None:
        return None
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError("price bound must be finite")
        return format(value, "f")
    if isinstance(value, str):
        return value
    raise TypeError("price bound must be Decimal or string, not float")

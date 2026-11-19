import argparse
import sys

from datahub_client import DataHubClient, DataHubError


def main() -> int:
    parser = argparse.ArgumentParser(description="Read latest-state Data Hub prices, news and source status.")
    parser.add_argument("--base-url", required=True, help="Data Hub backend URL")
    parser.add_argument("--timeout", type=float, default=5.0, help="HTTP timeout in seconds")
    parser.add_argument("--page-size", type=int, default=10)
    parser.add_argument("--max-pages", type=int, default=2)
    parser.add_argument("--source-id")
    parser.add_argument("--instrument-id")
    parser.add_argument("--sort-by", default="price")
    parser.add_argument("--sort-direction", choices=("asc", "desc"), default="desc")
    parser.add_argument("--min-price", help="Decimal text, without exponent")
    parser.add_argument("--max-price", help="Decimal text, without exponent")
    parser.add_argument("--keyword")
    parser.add_argument("--category")
    parser.add_argument("--published-from", help="Inclusive yyyy-MM-dd")
    parser.add_argument("--published-to", help="Inclusive yyyy-MM-dd")
    args = parser.parse_args()

    try:
        with DataHubClient(args.base_url, timeout=args.timeout) as client:
            print("Sources (separate request, not an atomic snapshot):")
            for source in client.sources()["sources"]:
                print(f"  {source['sourceId']}: {source['status']}; "
                      f"synced={source['syncedSinceStart']}; restored={source['restoredFromCheckpoint']}; "
                      f"lastUpdate={source['lastUpdateReceivedAtUtc']}; "
                      f"error={source['lastError']}; records={source['recordCount']}")

            filters = dict(source_id=args.source_id, instrument_id=args.instrument_id, sort_by=args.sort_by,
                           sort_direction=args.sort_direction, min_price=args.min_price, max_price=args.max_price)
            page = client.prices(page_size=args.page_size, **filters)
            print(f"Prices page {page['page']}/{page['totalPages']} (total {page['totalCount']}):")
            for item in page["items"]:
                print(f"  {item['sourceId']}/{item['instrumentId']} {item['price']} "
                      f"{item['currency']} [{item['sourceStatus']}]")
            if args.source_id and args.instrument_id:
                item = client.price(args.source_id, args.instrument_id)
                print(f"Exact price: {item['sourceId']}/{item['instrumentId']} {item['price']} {item['currency']}")
            if args.instrument_id:
                detail = client.instrument(args.instrument_id, page_size=args.page_size)
                print(f"Instrument {detail['instrumentId']} source observations "
                      f"({detail['totalCount']} total; as-of {detail['serverTimeUtc']}):")
                for item in detail["observations"]:
                    print(f"  {item['sourceId']} {item['providerSymbol']} {item['price']} {item['currency']} "
                          f"[{item['sourceStatus']}] synthetic={item['isSynthetic']}")
                memberships = client.memberships(args.instrument_id, page_size=args.page_size)
                print(f"  Memberships from {memberships['sourceId']} [{memberships['sourceStatus']}] "
                      f"({memberships['totalCount']} total):")
                for item in memberships["items"]:
                    print(f"    {item['indexId']} weight={item['weight']} effective={item['effectiveDate']}")

            news = client.news(page_size=args.page_size, keyword=args.keyword, category=args.category,
                               published_from=args.published_from, published_to=args.published_to)
            print(f"News page {news['page']}/{news['totalPages']}; "
                  f"source {news['source']['sourceId']} [{news['source']['state']}]; "
                  f"lastSuccess={news['source']['lastSuccessfulFetchAtUtc']}; "
                  f"error={news['source']['lastError']}:")
            for item in news["items"]:
                print(f"  {item['title']} | precision={item['publicationTimePrecision']} "
                      f"instant={item['publishedAtUtc']} date={item['publishedDate']} "
                      f"original={item['originalPublicationTime']} warnings={item['mappingWarnings']}")

            batch = client.price_pages(max_pages=args.max_pages, page_size=args.page_size, **filters)
            print(f"Bounded price scan: {len(batch.pages)} page(s), "
                  f"{sum(len(p['items']) for p in batch.pages)} record(s)")
            if batch.may_have_more:
                print("Page limit reached; results may be incomplete.")
            print("Pages are separate latest-state requests; records may repeat or be missed.")
    except (DataHubError, ValueError, TypeError) as exc:
        print(f"Data Hub error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

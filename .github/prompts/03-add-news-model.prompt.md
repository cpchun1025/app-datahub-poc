---
description: 新增 bounded news cache、不同 native schema 嘅 mock news，同 NEWS tab
agent: agent
---

請閱讀現有指示、README、technical-spec 同相關程式碼。
先列短計劃，然後實作。

## 目標

令 PoC 展示兩種不同資料：
- Equity latest-state prices
- Bounded recent-news metadata cache

本階段只用 synthetic mock news，唔連 CNFIN 或 Yahoo。
FX、options、sentiment、全文搜尋引擎 out of scope。

## 1. Model boundaries

明確分開：
Source-native DTO → validation/mapper → internal typed record → API DTO。

保留現有 equity model，避免無必要全面重構。
新聞唔可以塞入 PriceRecord。

共通 metadata 可用小型 envelope/interface，
但唔建立通用 schema registry、任意 JSON store
或巨大 nullable record。

為 equity response 加明確 product type（如適合），
保留現有 API 相容性。

## 2. Mock news

建立一個 mock-news source，原始 DTO 刻意唔同 internal model，例如：
- id / headline / link
- section
- publication date 字串
- optional original publisher

經獨立 mapper 轉成 NewsRecord。

Synthetic 資料要有：
- 中文及英文標題。
- Missing category/time。
- 重複 article ID。
- 同一 article 更新標題。
- Date-only publication。
- 完整 timestamp。
- Invalid URL/time 嘅 mapping error 測試案例。

Mock 運行預設提供有效資料；
malformed cases 可放 tests 或明確 fault mode。

## 3. News record

至少：
- SourceId
- ArticleId
- Title
- Url
- Publisher
- OriginalPublisher（optional）
- Category（optional）
- PublishedAtUtc（只有可靠 instant 先填）
- PublishedDate（date-only 情況）
- PublicationTimePrecision
- 原始 timezone/time representation（如需要）
- FirstSeenAtUtc
- FetchedAtUtc
- Language
- Mapping warnings

唔好用 FetchedAtUtc 冒充 publication time。
ArticleId 唔使用 title hash，避免改標題就變新文章。

Key = (SourceId, ArticleId)。

## 4. Bounded NewsStore

預設：
- MaxItems = 500
- RetentionDays = 7

按 FirstSeenAtUtc 清理 cache age，並寫清楚呢個語義，
避免修改標題或重複 fetch 無限延長保存時間。
達到 age/count 任一限制就 eviction，順序 deterministic。

要求：
- 同 ID upsert，唔重複。
- 列表冇再出現某文章唔代表 deletion。
- Fetch failure 保留 cached data。
- 唔保存 article history 或原始 tick。
- News ingestion 同 prices ingestion 互相隔離。

## 5. Status semantics

分開：
- LastAttemptAtUtc
- LastSuccessfulFetchAtUtc
- LatestArticlePublishedAtUtc（如有）
- LastError
- Cached item count

成功 fetch 但冇新文章仍然可以係正常。
News freshness 唔使用「最近新文章時間」判斷連線健康。

共享現有 monitoring/retry 基礎可以，
但唔硬套 price revision/snapshot-removal contract。

Mock outage button 只對 mock source 生效。

## 6. Persistence

News 加入 checkpoint/recovery，保留 bounded semantics。

檢查現有格式並提供：
- Schema version。
- 舊 checkpoint 明確 migration/support policy。
- 舊 price-only checkpoint 可恢復價格，
  news 由空 cache 開始，或提供清楚安全替代方案。
- Unknown future schema 唔静默當成正常空檔。
- Restored news 唔未同步就顯示已確認連線正常。
- 壞檔、backup fallback regression tests。

唔無聲覆寫唯一可用嘅舊 checkpoint。

## 7. API/UI

新增 /api/news：
- Pagination
- Keyword、category、publication date range filters
- Publication time sorting
- Unknown publication time 明確排序規則

新增 EQUITIES | NEWS tabs。
Prices API/UI 保持可用。

News table：
- Published
- Source
- Category
- Headline

要求：
- 標題開原文，新 tab 使用安全 rel。
- 外部內容純文字 render，唔注入 HTML。
- 僅允許有效 HTTP(S) links。
- Details panel 顯示 normalized metadata 同 mapping warnings。
- 保留 terminal design、keyboard access、loading/error/empty states。
- Header 唔假設所有 sources 都係 prices；
  source counts/status captions 要準確。

本階段唔新增真實新聞 CSV export，
避免未確認內容使用條款就擴大再分發。

## 8. Verification

新增 mapper/store/retention/status/checkpoint/API/UI logic tests。
執行現有及新增測試、build、lint。
可用 browser tools 時驗證 tabs 同 news 操作。

更新 README、technical-spec、folder structure、
預設 source 數量、API 同驗證狀態。
完成後總結，唔自動開始 CNFIN integration。
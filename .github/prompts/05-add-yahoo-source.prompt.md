---
description: 新增 optional Python yfinance API，同 .NET equity polling adapter
agent: agent
---

請閱讀專案指示、README、technical-spec，
並檢查 source registration、revision ordering、
snapshot merge/removal、checkpoint recovery 同 equity UI。

先提供短計劃，然後分小步實作。
唔全面重寫現有 source runner。

## 1. Architecture

Yahoo Finance
→ Python yfinance + FastAPI service
→ .NET Yahoo polling adapter
→ internal equity latest-state store
→ existing API/UI

- Python service 係 optional。
- 預設仍然 mock-only。
- Browser 唔直接連 Yahoo 或 Python provider。
- 唔由 .NET 每次 poll spawn Python process。
- 唔新增 database、Redis 或 message broker。
- 唔為呢個 adapter 建立通用 microservice platform。

## 2. Provider service

新增獨立目錄，例如 providers/yahoo/，包括：
- FastAPI app
- Pinned dependencies / reproducible installation
- Dockerfile / .dockerignore
- Configuration example
- Synthetic fixture-based tests
- README

提供最小 endpoints：
- Health：process health 同 upstream fetch status 分開
- Latest：configured watchlist 嘅 cached latest available bars

Provider response 刻意保留 provider-native schema，
由 .NET mapper 轉成 internal equity model。

要求：
- Scheduled/background fetch + cache。
- Consumer HTTP requests 唔直接觸發 Yahoo calls。
- Blocking yfinance operations 唔阻塞 FastAPI event loop。
- Bounded concurrency、timeout strategy、backoff。
- 避免 timeout 後不斷產生仍然運行嘅 background jobs。
- 一個 watchlist cycle 完成／失敗後先開始下一輪。
- 預設 fetch interval 300 秒。
- 唔假設 yfinance 有 SLA 或官方 API contract。
- 唔接受任意公開 URL 或無限制 symbols input。

## 3. Watchlist

設定檔提供以下起步例子，ticker 一律 string：
HK: 0700.HK, 0005.HK
CN: 600519.SS, 000001.SZ
TW: 2330.TW, 2317.TW
KR: 005930.KS, 000660.KS
JP: 7203.T, 6758.T

Agent 要驗證可取得嘅欄位同 symbol coverage，
唔可以假設每個市場結果一致。

保留：
- ProviderSymbol
- Stable internal instrument identity
- Market / Exchange（有可靠依據先填）
- Currency（缺失唔可以默認 USD）

唔做全市場 symbol discovery。

## 4. Price semantics

第一版使用 latest available bar close，唔聲稱 real-time tick quote。

- 明確設定 interval 同 lookback，
  優先選一個五個市場都合理支援嘅方案。
- yfinance options 明確設定，唔依賴隱含預設；
  auto_adjust=False，文件說明其價格基準，
  唔聲稱呢個設定保證所有 corporate-action 語義。
- 保存 PriceKind=BarClose、BarInterval、
  upstream bar timestamp/date、FetchedAtUtc、ReceivedAtUtc。
- 區分 bar start/end；唔虛構來源未提供嘅 quote time。
- 轉 UTC 時保留正確交易所時區。
- 冇數據、NaN、Infinity 或 invalid value 唔可映射成 0。
- 如果 interval 不可用，回報明確 error；
  唔靜默改成 daily close。
- 如果需要 fallback，必須明確設定及標示。

## 5. Response completeness

Provider 回應包括：
- Requested symbols
- Successful records
- Per-symbol errors
- Fetch timestamps
- Completeness information

10 個 symbols 回傳 8 個，
唔可以將另外 2 個當成 delisted。

Watchlist membership 係設定決定，
唔以 partial fetch missing rows 觸發 snapshot deletion。
失敗 symbol 保留 last-known value 同 stale/error metadata。
唔 fallback 去 mock 再標示為 Yahoo。

## 6. Ordering and recovery

Yahoo 冇現有 mock 嘅 monotonic revision contract。

設計並測試：
- Local ingestion ordering。
- 同一 bar 可能被修訂，timestamp 相同唔代表值一定相同。
- Python provider restart counter reset。
- .NET backend restart + checkpoint restore。
- Provider 暫時回舊 cached bar。
- Partial fetch。
- Empty response。

唔用 timestamp ticks 冒充權威 sequence。
本地 revision 必須標示為 local，唔係 Yahoo revision。

可以使用具明確 epoch/session 嘅 ordering
或其他有測試支援嘅方案；
唔可以令 checkpoint 高 revision 永遠拒絕重啟後新值。

## 7. Status

分開：
- Provider reachable
- Last successful upstream fetch
- Per-symbol data age
- Quote/bar time

Provider cache API 回 200 唔代表 upstream 剛成功。

市場休市唔代表 disconnected；
但唔好只靠星期幾就聲稱知道交易所假期。
冇可靠 calendar 時顯示 market state unknown。

UI 唔可以將 source LIVE 解讀成 real-time market price。
顯示 BAR CLOSE / interval / as-of / possible delay。

## 8. Integration

- 明確配置 Yahoo Enabled。
- 現有 UseMockSources 行為按實際需求調整，
  保留預設 mocks，唔誤導成真實來源全部可用。
- Docker Compose profile，例如 yahoo：
  default compose 仍然只啟動 backend/frontend。
- 同一 opt-in workflow 要啟動 provider
  並啟用 backend adapter，唔留下只起 provider 嘅半套設定。
- 提供本機 Python/.NET/frontend 獨立啟動步驟。
- Provider 唔公開暴露；如需 host port，綁 loopback。
- Yahoo failure 唔阻止 backend health 或 mock ingestion。

## 9. UI/query

Equities grid 增加可靠 metadata：
- Market
- Exchange（optional）
- Provider symbol
- Price kind
- As-of
- Data age/quality

支援 server-side market filter。
Mock 冇 market 嘅資料保持 unknown，
唔虚構 metadata。

CSV 同步新增欄位及語義；
真實資料 export 加 research-use 提示，
唔宣稱可再分發。

## 10. Tests/docs

Python tests 使用 synthetic mocked responses。
.NET tests 包括 mapping、partial failure、ordering、
同一 bar 修訂、restart/recovery、freshness。
預設測試唔連 Yahoo。

可明確啟用 live smoke test；
記錄每個市場成功／失敗，唔聲稱全市場 coverage。

README 更新：
- Optional external-source architecture。
- Prerequisites、ports、config、Compose profile。
- 價格基準、延遲、coverage、限流限制。
- yfinance 非 Yahoo 官方認可 SDK。
- Code license 與 data usage rights 係兩回事。
- 真實數據、checkpoints、exports 唔 commit。
- 所有實際驗證及未驗證項目。

執行相關 build、lint、tests；
唔聲稱執行過未做嘅驗證。
---
description: 按已確認 discovery 實作 optional CNFIN news metadata adapter
agent: agent
---

請閱讀：
- 專案指示、README、technical-spec
- doc/cnfin-discovery.md
- News ingestion、mock news、store、status、checkpoint 同 UI

## Prerequisite

如果 discovery 缺失、存取方式未確認、
或網站使用限制仍然 blocked：
只列出阻礙及需要確認事項，停止正式接駁。
唔虛構 endpoint、selector 或使用授權。

## Goal

新增 optional CNFIN metadata source。
預設停用，mock-only 啟動繼續可用。

唔抓全文、圖片、附件。
唔做 AI 摘要、sentiment 或 article archive。

## Implementation

- 優先使用 discovery 確認嘅官方公開 feed/API。
- 如採用 HTML，使用 .NET HttpClient + 合適 HTML parser，
  例如 AngleSharp；唔以 regex 解析完整 HTML。
- Native DTO、parser 同 internal mapper 分開。
- 唔新增 Python service 或 browser runtime。
- 如果只有 browser automation 可行，
  停止並提出成本／風險，唔自行加入。
- 固定來源位址，唔接受任意 user-supplied fetch URL。
- Redirect 目的地要按 discovery 設定 allowlist，
  唔允許抓去 loopback/private/internal network。
- Timeout、response-size limit、cancellation。
- 每 source 最多一個 in-flight request。
- 預設 polling 600 秒，按網站要求可調整。
- 支援時使用 ETag/Last-Modified。
- 429 尊重 Retry-After，其他 transient errors backoff。
- 403/CAPTCHA 唔繞過；停止密集重試並回報狀態。

## Parsing and mapping

按 discovery 實際確認欄位實作：
- Stable article ID，否則 canonical URL hash。
- 正確處理 relative links。
- 唔任意刪 query parameters，避免合併不同文章。
- Title、publisher、original publisher、category、language。
- 發佈時間 precision/timezone handling。
- 冇可靠時間就保留 null/date-only，唔虛構 instant。

Parser 結構失配唔可以當成成功嘅 empty list。
Partial parse 要回報 warnings/counts，
唔可以覆蓋或清空之前 cache。

Article 離開列表唔代表刪除。
Freshness 按成功抓取／驗證回應，
唔按最新文章日期。

## Config/UI

- 明確 Enabled flag。
- App 啟動唔因 CNFIN 暫時失敗而失敗。
- 顯示 EXTERNAL / CNFIN，唔標 MOCK。
- 唔提供 mock outage control。
- 顯示 last successful fetch 同 error。
- 預設混合來源時 header 用準確 MOCK/MIXED 等標籤，
  唔將全部來源聲稱 mock。
- 唔新增全文展示或真實新聞 CSV export。

## Tests and verification

- Parser tests 用自製 synthetic HTML，
  唔 commit 真實新聞頁面。
- 測試 missing fields、date-only、timezone、
  malformed structure、duplicates、partial failures。
- Http handler tests：304、429、timeout、oversized response、
  disallowed redirect。
- Default CI/tests 唔連真網站。
- 如 discovery 允許且使用者明確啟用，
  可少量執行 live smoke test；
  記錄日期、結果及限制，唔保存真實 payload 入 repo。

更新文件：
- 啟用／停用步驟。
- Polling policy。
- 來源及原文連結 attribution。
- 使用限制及未驗證事項。
- Mock-only 同 external mode 區別。

唔聲稱技術抓取成功等於取得再分發授權。
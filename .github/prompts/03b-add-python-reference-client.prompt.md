---
description: 建立獨立 Python reference client，驗證 Data Hub API-first 使用方式
agent: agent
---

請實作小型 Python non-dashboard reference client。

先閱讀：
- .github/copilot-instructions.md
- README.md
- doc/technical-spec.md
- doc/api-first-review.md，包括 follow-up
- 實際 API endpoints、DTO、OpenAPI 及 HTTP integration tests
- git status

先提供短計劃，再分小步實作。
保留所有現有修改，不覆蓋無關檔案。

## 1. Goal

本專案是 API-first、多來源、多模型 Data Hub。
Python client 是 Dashboard 以外的第二個 API consumer。

本次只使用目前已交付的：
- Prices
- News
- Source status

不實作未來模型或 external sources。
不開始 Prompt 04 / 05。

## 2. Structure

放在 clients/python/，與 providers/ 分開。

提供：
- pyproject.toml
- src/datahub_client/
- examples/
- tests/
- README.md
- 合適的 .gitignore 規則

使用 src layout，可透過 pip 安裝。
選擇少量、穩定 dependencies，記錄支援的 Python 版本
及可重現的安裝方式。

不建立完整 SDK、code generation、
package publishing 或 generic framework。

## 3. API boundary

只經 Data Hub 公開 HTTP API：
- 不讀 backend store、checkpoint 或其他 runtime files。
- 不 import backend implementation。
- 不依賴 React、frontend dev server 或 UI state。
- 不直接呼叫 Yahoo、CNFIN 或其他 external sources。
- 不重新實作 mapping、retention、source freshness 判斷。

以實際 API contract 決定 methods、參數及 endpoint 路徑，
不要虛構欄位或 endpoints。

如果發現 backend contract 問題，
列出證據及最小修正建議，不在 client 靜默補救；
涉及 breaking change 時停止該項並等待確認。

## 4. Minimal capabilities

提供簡單同步 client，優先使用 httpx.Client：
- 可設定 base URL 與有限 timeout。
- 正確管理連線資源，支援 context manager。
- 查詢 source status。
- 查詢 prices：示範已支援的 filters、
  sorting、pagination 及單筆查詢。
- 查詢 news：示範已支援的 filters 及 pagination。

提供可執行的 CLI 或 examples：
- 列出 sources/status。
- 查詢一頁 prices。
- 查詢一頁 news。
- 示範有上限的多頁讀取。

不必同時建立兩套重複的 CLI/example framework。
不提供 admin snapshot/checkpoint 操作。

## 5. Data semantics

### Decimal

保留 backend 現有 JSON number contract。

需要精確的 decimal 欄位：
- 直接由原始 JSON number token 解析成 Decimal，
  例如 json.loads(..., parse_float=Decimal)。
- 不先轉 binary float 再轉 Decimal。
- 整數形式的 decimal 欄位亦要按 schema 正確處理。
- 不將所有 integer 欄位一律轉為 Decimal。

若使用 validation/model library，
確認它不會在 decimal-aware parsing 前先轉成 float。

輸出以簡單文字為主。
如提供 JSON output，明確記錄 Decimal 序列化政策；
不可轉 float 後聲稱無損。

### Time and status

- 保留 null、date-only、unknown timezone、
  publication precision 等實際 contract 語義。
- 不把 date-only 或 unknown timezone 強行轉成 UTC instant。
- 不把 fetched time 當 publication/bar time。
- 不把來源 Live 解讀成每筆資料都是即時市場價格。
- 顯示 API 提供的 health/recovery/quality，
  不自行推斷缺少的狀態。

### Pagination

- 每次 request 有有限 page size。
- 多頁 helper 必須有明確 max_pages 或 max_records 上限。
- 達到上限時明確提示可能未讀完。
- 不一次無限制載入全部資料。
- 不宣稱 stable sorting 保證跨頁 snapshot consistency。
- 記錄 latest-state 在多次 request 間可能改變，
  因而可能重複或遺漏 records。
- 若 source status 與資料分開取得，
  不宣稱兩者是同一原子快照。

## 6. Error handling

清楚區分：
- Connection failure / timeout。
- HTTP validation / server error。
- 有效的 empty result。
- Invalid JSON / 不符合必要 contract 的 response。

正確處理 ProblemDetails，
亦容許 server/proxy 回傳非 JSON error body，
避免 error handling 自己再次 crash。

不建立無限制 retry。
第一版可以不自動 retry，文件清楚說明即可。

CLI/examples 失敗時提供可理解訊息及非零 exit code。

## 7. Tests

預設 pytest 完全離線，
使用 synthetic HTTP responses，例如 httpx.MockTransport。

至少測試：
- Prices/news/status parsing。
- Filters、query parameters、pagination 上限。
- Empty results。
- Null、date-only、unknown time semantics。
- Decimal 精度，包括會因 float round-trip 損失精度的值。
- Decimal 欄位以整數 JSON token 表示。
- Stale/recovering 狀態保留。
- ProblemDetails、非 JSON error response。
- Timeout、connection failure、malformed response。

不要用 mock tests 聲稱已驗證真實 backend 相容性。

另提供 opt-in localhost smoke test：
- 連接實際獨立啟動的 mock-mode backend。
- 不需要 frontend。
- 不啟用 external sources。
- 明確設定 base URL，避免誤連其他環境。
- 不操作 admin endpoints。
- 不將 runtime responses/checkpoints 保存到 repo。

如果環境不允許 listener/socket，
記錄具體限制並提供使用者本機執行步驟；
不得標示 end-to-end 已通過。

## 8. Docs

clients/python/README.md 包括：
- Python prerequisites。
- Windows PowerShell 與適用的 POSIX 安裝／執行指令。
- 如何只啟動 mock backend，不啟動 frontend。
- Base URL、timeouts、commands/examples。
- Tests 與 opt-in smoke test。
- Decimal、時間、pagination consistency 限制。
- Python client 與 optional Yahoo provider 的分別。

主 README 加入 client 入口連結。
technical-spec 按實際交付更新 API consumer 架構，
不宣稱未驗證能力。

## 9. Restrictions

- 不新增常駐 service、database 或 broker。
- 不改 default Compose。
- 不新增 frontend dependency。
- 不升級 unrelated dependencies。
- 不保存真實市場／新聞資料、secrets 或個人路徑。
- 不 commit / push。

## 10. Completion

執行 Python tests 及相關驗證。
如果修改到 backend/frontend，執行相應 regression tests。

最後列出：
1. 新增／修改檔案。
2. 可直接複製的啟動與使用指令。
3. 實際執行的驗證及結果。
4. Mock HTTP tests 與 live localhost smoke test 的分別。
5. 未驗證項目及已知限制。

完成後停止，等待確認。
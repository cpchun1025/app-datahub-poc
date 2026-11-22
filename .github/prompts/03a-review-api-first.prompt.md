---
description: 審查截至 Prompt 03 的 API-first、多模型架構及 client readiness
agent: agent
---

目前 Prompt 03 已完成。
請審查現有 repository，不要開始 Prompt 04。

本次只 review，不修改程式碼、設定或現有文件。
唯一允許新增的交付檔案是：
doc/api-first-review.md

可以執行非破壞性本機 tests/build。
不要啟用 external sources、刪除 checkpoints/volumes、
自動修正 dependencies、commit 或 push。
保留所有現有未提交修改。

## 1. 先閱讀及核實

- .github/copilot-instructions.md
- README.md
- doc/technical-spec.md
- .github/prompts/02-upgrade-grid.prompt.md
- .github/prompts/03-add-news-model.prompt.md
- 實際 backend/frontend 程式碼、API contracts、
  source registration、stores、checkpoint 及 tests
- git status

不要依賴之前 session。
README/technical-spec 是待核實聲稱，
不能代替閱讀程式碼及實際測試結果。

本次範圍截至 Prompt 03。
不要把未實作的 CNFIN、Yahoo 或未來產品列為缺陷。

## 2. 原始目的：API-first Data Hub

核心交付是向下游 systems/clients 提供
多來源、多模型的資料 API。

Dashboard 是其中一個 API consumer，
以及操作／觀察工具，不是系統唯一目的。

目前提供：
- Prices latest-state
- Bounded recent-news metadata cache

目前不是完整 data lake、歷史資料庫、
原始 payload archive 或任意 analytics query engine。
不要因「data lake API」的願景而擴大本次基建範圍。

未來可能提供：
- Index constituents
- ETF PCF compositions
- Interest rates
- Repo rates
- Yield curves
- Barra risk / factor exposures
- Risk platform PnL
- Trading alpha signals

本次不實作以上未來模型，不建立空 endpoints/tabs。

分清楚：
- Data product / model
- Source system / provider
- Transport / ingestion mechanism

Solace 是 transport，不是固定資料模型。

## 3. Review：Backend 是否真正獨立

核實完全不啟動 frontend 時：
- Backend 可以啟動並持續 ingestion。
- Prices/news 可以透過 HTTP API 查詢。
- Retention、checkpoint、recovery 正常運作。
- Source status 可以透過 API 取得。
- 不需要 browser session、UI polling 或打開某個 tab。

Dashboard pause、關閉或斷線，
不得停止 backend ingestion 或 maintenance。

## 4. Review：Public API contract

核實非 UI client 能否只使用 HTTP API，
而不讀 backend files、checkpoint 或理解 React state。

檢查：
- Prices/news response DTO 及 OpenAPI 是否與實際一致。
- Native DTO → mapping → internal model → API contract 邊界。
- 不要求純粹為分層而重複相同 DTO，
  但要指出實際耦合或意外暴露 internal state 的風險。
- Identity、nullable fields、時間與精度語義。
- Filters、sorting、stable tie-breakers、null ordering。
- Pagination、page-size limits、validation、problem+json。
- Empty results、來源失敗但有 cache、restored/stale data 行為。
- Source health 與 per-record age/quality 是否可區分。
- 既有 prices API 相容性。
- 跨請求資料會變動，是否有誤稱 snapshot consistency。
- API clients 能否取得與資料相關的 source status，
  並理解分開請求的 consistency 限制。

News 特別檢查：
- FetchedAtUtc 不冒充 publication time。
- Unknown timezone 不被虛構成 UTC instant。
- Article 離開列表不觸發錯誤刪除。
- Fetch success 不等於有新文章。
- Cached/restored data 不被誤標成已確認健康。

## 5. Review：多模型擴展邊界

辨別兩類設計：
- 合理留在 price-specific 模組的 price assumptions。
- 不應寫死在 shared infrastructure 的 price-only assumptions。

檢查 API、source registration、status、checkpoint、
shared services 及 UI shell 是否強迫所有產品都有：
- InstrumentId
- Price / Currency
- Price up/down
- 相同 revision contract
- 相同 snapshot deletion/retention/freshness semantics

未來模型可能是：
- Record：rate observation。
- Collection：index constituents、PCF basket。
- Aggregate：yield curve + nodes。

評估能否透過局部新增模組支援，
而不是要求目前立即建立通用 framework。

注意：
- Latest-state 的單位可能是整個 collection/aggregate。
- 未來可能需要版本完整發布，避免混合不同版本的成員。
- Partial response 不一定代表 membership deletion。
- Latest received 不等於 currently effective。
- Observation/valuation time、business date、
  effective date、received time 不應混為一談。

只將目前的實際問題列為 defects。
合理的未來工作列為 extension notes，
不要把「尚未支援所有金融產品」當成 bug。

## 6. Review：截至 03 的 correctness/regression

針對性核實：
- 三個 mock price sources 是否保持正常。
- Snapshot/update ordering 與 membership。
- Grid filter → sort → pagination。
- CSV semantics、escaping、formula injection、row limit。
- Pause display 時 connectivity monitoring 是否仍正常。
- News mapping、upsert、bounded retention、
  FirstSeenAtUtc 及 fetch failure cache preservation。
- Checkpoint schema、price-only compatibility、
  unknown schema、corruption/backup fallback。
- Restored prices/news 的 status。
- News failure 不阻止 prices ingestion，反之亦然。

不要只列測試名稱；檢查測試是否真的驗證相關行為。

## 7. Review：Client readiness 與安全界線

- 是否有直接驗證 HTTP API 的 integration tests？
- README 是否提供不開 frontend 的啟動及 curl 範例？
- OpenAPI 是否足夠讓另一個語言的 client 使用？
- Decimal precision 及時間欄位的 wire representation
  是否有清楚 contract？
- 未認證 API/admin endpoints 的部署與網絡暴露限制
  是否準確記錄？
- 不將可呼叫的 PoC API 描述成 production-ready。

## 8. Python reference client 建議

只提出計劃，本次不實作。

建議放在 clients/python/，不是 providers/。
它是獨立 API consumer：
- 只經公開 HTTP API。
- 示範 prices、news、source status。
- 示範 pagination、filters、unknown/stale/recovering。
- 處理 timeout、斷線及 API errors。
- 無 GUI，不讀 checkpoint，不依賴 frontend。
- 不重新實作 backend 的 business rules。
- 預設 synthetic/mock-only。
- 不新增常駐 service 或改 default Compose。
- 不建立完整 SDK/publishing platform。

指出實作此 client 前真正需要修正的 blockers；
若没有 blockers，明確說明可以直接開始。

## 9. Output

建立 doc/api-first-review.md。

先提供：
- API-first readiness 簡短結論。
- 已確認符合的能力。
- 是否需要調整方向，或只是補 contract/docs/tests。

Findings 按 Critical / High / Medium / Low 排列：
- 分類：confirmed bug / potential risk / unverified claim。
- 檔案及行號。
- 問題及實際影響。
- 證據、重現／驗證方式。
- 最小修正建議。

沒有證據不要虛構 findings。
未來 enhancement 與 defects 分開列。

最後列：
1. Python client 前必須修正。
2. 可與 Python client 一起補充。
3. 可延後的多模型擴展。
4. 實際執行的命令及結果。
5. 未驗證事項及原因。
6. 建議下一個最小 implementation task。

完成後停止，等待使用者確認。
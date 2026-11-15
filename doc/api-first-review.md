# API-first Review（截至 Prompt 03）

審查日期：2026-11-15

## 結論

現有 PoC 已具備 API-first 的核心運作方式：Backend 可在沒有 frontend 的情況下執行 ingestion、news fetch、retention 與 checkpoint；Prices、News 及來源狀態可經 HTTP 取得。Dashboard 是 API consumer，而非 ingestion 的啟動條件。

**無需調整整體方向。** 目前真正需要補的是 OpenAPI contract 的完整度，以及少量跨語言 client 的精度說明／文件同步；未見需要重做 store、ingestion 或加入新基建的證據。Python reference client **沒有必須先修正的 runtime blocker，可以直接開始**；建議將 OpenAPI 修正與 client 一併處理。

## 已確認符合的能力

- Backend hosted services 會自行啟動 price source runners、news fetch service、checkpoint 與 recovery；不依賴 UI polling。DI 與 endpoint 分別見 [ServiceCollectionExtensions.cs](../backend/src/DataHub.Api/Hosting/ServiceCollectionExtensions.cs#L25) 及 [DataHubEndpoints.cs](../backend/src/DataHub.Api/Endpoints/DataHubEndpoints.cs#L25)。
- API 經 HTTP 提供 prices、source status、news metadata；News 回應亦附帶該 news source 的 fetch 狀態。HTTP integration tests 使用完整 `Program`／DI／host pipeline 的 TestServer，並非只測 React 或直接呼叫 store；見 [DataHubFactory.cs](../backend/tests/DataHub.Api.Tests/Support/DataHubFactory.cs#L12)、[ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs#L66) 及 [NewsTests.cs](../backend/tests/DataHub.Api.Tests/News/NewsTests.cs#L146)。
- Price store 以 source 分隔 identity，revision、snapshot membership、filter → sort → pagination、CSV 查詢語義及跨來源 fuzzy consistency 均有實作／測試；跨請求不是 snapshot 的限制亦有文件說明。News 使用獨立 `MockNewsItem → NewsMapper → NewsRecord → NewsDto` 邊界、bounded store、first-seen retention、publication precision 及自己的 fetch status；不是把新聞塞進 `PriceRecord`。
- Recovery 使用 checkpoint schema 2 並保留 schema 1 price-only 相容；舊資料在今次 snapshot／fetch 成功前分別維持 Recovering，News／price mock outage 的失敗隔離亦有 tests。
- README 已清楚標示這是 mock-only PoC、Admin API 未認證及 loopback 部署限制；不應描述為 production-ready。

## Findings

### Critical

沒有找到有證據支持的 Critical finding。

### High

沒有找到有證據支持的 High finding。

### Medium

#### M-1 — OpenAPI 未完整描述 News、CSV 及部分錯誤 response

- **分類：** confirmed bug（API contract/documentation）
- **位置：** [NewsEndpoints.cs](../backend/src/DataHub.Api/News/NewsEndpoints.cs#L25)、[DataHubEndpoints.cs](../backend/src/DataHub.Api/Endpoints/DataHubEndpoints.cs#L44)、[ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs#L259)
- **問題及影響：** `/api/news` 以 `HttpRequest.Query` 手動解析 query，route 只設定 tag/name，沒有宣告 query parameters 或 `NewsListResponse` response schema。Live GET `/openapi/v1.json` 核對結果為 `/api/news` `parameters: []`、`200` 只有 description、沒有 response content/schema。相同文件中，`/api/prices` 沒有宣告實際會回傳的 unknown-source `404`；`/api/prices/export.csv` 的 `200` 亦沒有輸出 `text/csv` response content。另一語言的 client 或 OpenAPI generator 因而不能可靠發現 News query/DTO、CSV media type 或完整錯誤合約。
- **證據／驗證：** 實際啟動本機 mock-only Backend 並讀取 `/openapi/v1.json`；觀察到 `/api/prices` 有 200/400 而無 404、CSV 200 沒有 content metadata。程式中的 News route 與 handler 可見於 [NewsEndpoints.cs](../backend/src/DataHub.Api/News/NewsEndpoints.cs#L27)，unknown source 分支見 [DataHubEndpoints.cs](../backend/src/DataHub.Api/Endpoints/DataHubEndpoints.cs#L123)。現有 OpenAPI integration test 只檢查列出的 endpoints 是否存在，expected list 未包括 News 或 CSV，也沒有驗證參數、response schema/status/media type，見 [ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs#L259)。
- **最小建議：** 為 News query parameters 與 `NewsListResponse` 加明確 OpenAPI metadata；明確描述 Prices 的 404、CSV 的 `text/csv` response；擴充 OpenAPI integration test 驗證參數、DTO schema、status codes 及 media type。這是 contract readiness 改善，不阻止目前 HTTP API 使用。

### Low

#### L-1 — Price JSON number 對不同語言 client 的精度約定仍不夠明確

- **分類：** potential risk
- **位置：** [Dtos.cs](../backend/src/DataHub.Api/Endpoints/Dtos.cs#L5)、[README.md](../README.md#L437)、[technical-spec.md](./technical-spec.md#L758)、[technical-spec.md](./technical-spec.md#L1291)
- **問題及影響：** Backend 的 `PriceDto.Price` 是 `decimal`，JSON wire value 是 number；現有文件說明保留 decimal scale，但未定義跨語言 client 的精確解析方式或可依賴的精度邊界。Python 標準 `json` decoder 預設會將小數解析為 binary `float`，可能使高精度價格在 client 端失去十進位精確性。Technical spec 已指出 browser 的 `double` 限制；這是 downstream parsing risk，不是目前 Backend decimal/store 的已確認錯誤。
- **證據／驗證：** API integration test 確認 `price` 是 JSON number，見 [ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs#L236)；Frontend 型別亦是 `number`，而 technical spec 已將 JS double 列為限制，見 [types.ts](../frontend/src/api/types.ts#L6) 及上述規格位置。
- **最小建議：** Python reference client 用 `Decimal` 解析 JSON 小數，並在 client 文件註明；同步說明 API 的 decimal wire representation／精度邊界。毋須為本次 review 改變既有 API 表示方式。

#### L-2 — README／technical spec 的 frontend test 數字不一致且已落後

- **分類：** confirmed bug（verification documentation）
- **位置：** [README.md](../README.md#L165)、[README.md](../README.md#L202)、[README.md](../README.md#L523)、[technical-spec.md](./technical-spec.md#L1116)、[technical-spec.md](./technical-spec.md#L1500)
- **問題及影響：** README 指令註解與 technical spec 記載 97 個 frontend tests；README 的 Prompt 3 驗證記錄則寫 101 個／12 files。本次實際執行結果是 106 個／14 files。讀者無法由目前文件辨識哪些數字是當前驗證狀態。
- **證據／驗證：** 本次 `npm test -- --run` 顯示 14 test files、106 tests passed；`npm run lint` 及 `npm run build` 亦成功。Backend README 的 Prompt 3 記錄為 218 tests，本次 Backend 執行亦為 218 passed。
- **最小建議：** 後續文件同步時更新目前 frontend test count；過往 Prompt 2／3 記錄若要保留，標清楚為當時版本數字，避免與目前總數混淆。

## Python reference client

建議放在 `clients/python/`，作為獨立 API consumer；只呼叫公開 HTTP API，不讀 checkpoint、不依賴 React，也不重做 Backend 的 business rules。可示範 prices、news、source status、pagination/filter、Recovering／Disconnected、timeout／API errors；以 mock/synthetic data 為預設，不新增常駐 service 或修改預設 Compose。

1. **開始前必須修正：** 沒有已確認的 runtime blocker；可以直接開始 Python client。
2. **可與 Python client 一起補充：** M-1 的 OpenAPI contract tests／metadata、README 的 `/api/news` curl 範例、Python `Decimal` parsing 說明，以及 L-2 的測試數字同步。
3. **可延後的多模型擴展：** 現有 `ISourceAdapter`／`SourceRunner`／`LatestStateStore` 是明確 price-specific；News 已用獨立 store、mapper、status 及 API。將來增加 rates、constituents 或 aggregate 時，局部加入 typed product model、identity、ordering、removal/retention/freshness 與 checkpoint compatibility 即可；collection/aggregate 要定義完整版本發布語義，並分清 observation/effective/received time。現時不需要建立通用 framework，也不應將這些未來模型列為 defects。
4. **實際執行的命令及結果：**
   - `backend`：`dotnet test DataHub.sln --no-restore` — 218 passed，0 failed。
   - `frontend`：`npm run lint` — 成功；`npm test -- --run` — 14 files／106 tests passed；`npm run build` — TypeScript build 與 Vite build 成功。
   - 暫時以 `dotnet run --no-build --project src\DataHub.Api --urls http://127.0.0.1:5099` 啟動 Backend，檢視 `/openapi/v1.json` 後停止；此為 mock-only，未啟用 external sources。
   - 初次由 repository root 執行 `dotnet test DataHub.sln --no-restore` 及 frontend npm scripts 因工作目錄錯誤而失敗（solution/package.json 位於子目錄）；改在 `backend`／`frontend` 正確目錄重跑後通過。
5. **未驗證事項及原因：** 本次沒有執行 Docker Compose、Visual Studio F5、瀏覽器操作、外部來源或負載測試；它們不是核實 API contract 與本地 tests 所必需。未驗證真實 upstream、production deployment、其他語言 client 的實際運作或任何 production readiness。
6. **建議下一個最小 implementation task：** 只補完整 OpenAPI metadata 與 HTTP integration assertions（News query/response、Prices unknown-source 404、CSV content type），並同步 README 的 client 範例／test count；完成後再實作 Python reference client。今次沒有開始該 task 或 Prompt 04。

## Follow-up（2026-11-16）

以下係原 review 之後的獨立修正與驗證；上面 findings 和原本驗證結果保留作歷史記錄。

| Finding | 狀態 | 修改位置及結果 | 剩餘風險 |
|---|---|---|---|
| M-1 | fixed | [NewsEndpoints.cs](../backend/src/DataHub.Api/News/NewsEndpoints.cs) 宣告七個 query 參數、typed 200／400 response；[DataHubEndpoints.cs](../backend/src/DataHub.Api/Endpoints/DataHubEndpoints.cs) 補 Prices 404、CSV `text/csv`、400／404／422 及限制描述。[ServiceCollectionExtensions.cs](../backend/src/DataHub.Api/Hosting/ServiceCollectionExtensions.cs) 修正自訂 UTC converter 產生的時間 schema 型別及 nullable；[ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs)、[NewsTests.cs](../backend/tests/DataHub.Api.Tests/News/NewsTests.cs) 經 HTTP／TestServer 驗證 schema、media type、錯誤及 query 相容性。 | 跨請求及跨來源仍非一致 snapshot；row cap 唔限制所有排序記憶體。冇用外部 OpenAPI generator 或真實 network listener 驗證。 |
| L-1 | documented limitation | [ApiReadTests.cs](../backend/tests/DataHub.Api.Tests/Api/ApiReadTests.cs) 驗證四個 synthetic decimal 的 list／detail 原始 JSON number；[README.md](../README.md) 同 [technical-spec.md](technical-spec.md) 說明 .NET decimal、wire 表示、JavaScript double 與 Python decimal-aware parsing。現有 number contract 不變。 | Consumer 仍可能將 number 轉 binary float、丟失 scale 或精度；測試只證明代表值的 wire literal，唔保證所有 client／計算無損。Python client 尚未實作或驗證。 |
| L-2 | fixed | [README.md](../README.md) 移除無日期指令註解的測試總數；[technical-spec.md](technical-spec.md) 改為穩定範圍描述，並將舊表格清楚標為歷史快照。 | 測試數目會持續改變；歷史結果唔代表現時或其他環境的通過狀態。 |

### 本次實際驗證

2026-11-16，Windows 本機 .NET SDK 10.0.404、Node v26.13.0／npm 11.20.0：

- `backend`：`dotnet test DataHub.sln --no-restore` — 226 passed、0 failed；backend 數目計 xUnit 展開後 test cases（包括 theory 資料列），命令亦編譯 backend。
- `frontend`：`npm test -- --run` — 14 test files、106 tests passed；Vitest 計 test files 同 test cases。
- `frontend`：`npm run lint` — 成功；`npm run build` — TypeScript type-check 同 Vite build 成功。
- 開發期間另執行 targeted HTTP／OpenAPI tests；初次有兩個測試假設與實際有效 OpenAPI 表示不符（optional `required` 可省略、enum 可只列 `enum`），修正 assertion 後重跑通過；最終完整 suite 亦通過。
- 嘗試獨立啟動 mock-only backend（checkpoint 停用）時，本機 loopback socket bind 回 10013 權限錯誤，程序已退出；HTTP／OpenAPI 改用 in-process TestServer 驗證。冇使用其他現有服務或刪除既有 checkpoints／volumes。

本次冇執行 Node 24、Docker Compose、Visual Studio F5、瀏覽器、真實外部來源或 live external tests；亦冇驗證跨語言 client 實際行為。原有 API 無需 breaking change；可以在獲使用者確認後開始 Python reference client，但本 follow-up 沒有開始該階段。


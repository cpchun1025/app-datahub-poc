# CNFIN news metadata technical discovery

**調查日期：2026-11-06（UTC+08:00）**  
**目前結論：blocked／未啟用；Prompt 1 調查已完成並獲使用者接受。** 公開 HTML 嘅 metadata parser 技術上可行，但未確認自動收集、保存、展示及再分發 metadata 嘅授權；唔應因此直接啟用 external ingestion。

本階段只有 technical discovery 同文件，**冇實作 CNFIN adapter、news model、ingestion service 或 UI**，亦冇改價格 ingestion、API、設定或 checkpoint schema。應用程式仍然係 mock-only。調查範圍按 [01-discover-cnfin.prompt.md](../.github/prompts/01-discover-cnfin.prompt.md)；現有系統見 [README](../README.md) 同 [technical-spec](technical-spec.md)。

**接受調查後嘅決定（2026-11-06）**：未確認 metadata 使用授權前，唔啟用正式擷取或 polling。時區、publisher、article ID／canonical URL 等未確認欄位維持 **unknown，唔自行推斷**；初稿提出嘅 local identity／time assumption／publisher default 並未採用。有限新聞取樣／覆蓋唔可描述為完整來源覆蓋。下面嘅證據仍然只來自 Prompt 1；Prompt 2 只更新呢個決定，冇再查網站或實作 adapter。

## 1. 方法、證據及界限

- 用 `web_fetch` 查閱公開頁面，再用 `curl.exe` GET 核對 HTTP status、redirect 同 Content-Type；冇提供登入資料、冇沿用登入 session、冇執行網站 JavaScript。
- 以下八個**不同 URL**都有實際查閱，部分因核對 headers 而讀取多於一次；唔係只靠搜尋結果或者 URL 命名推測。
- 新聞取樣只限一個列表首屏同一個後續 fragment。冇巡迴抓頁、冇開文章詳情、冇另外下載圖片／附件，亦冇處理全文、sentiment 或 AI 摘要。
- HTML response 本身包含摘要、圖片 URL 等非目標欄位；只檢查 metadata 結構，唔將摘要等變成資料集。下載嘅 HTML／script 只曾暫存於 workspace 以外供檢查，完成後清理；repo 冇收錄真實頁面、新聞標題樣本或 parser fixture。
- **confirmed** = 本次 response／檔案可直接支持嘅事實；**unknown** = 冇足夠證據；**proposal** = 建議，唔係已實作或網站承諾。單次成功唔代表長期穩定或者獲授權。

### 實際查閱 URLs

以下全部係直接 HTTPS GET 回 **200**；核對時冇開啟自動跟隨 redirect，亦冇收到 redirect response。Content-Type 都帶 `charset=utf-8`，下載 bytes 亦全部通過 strict UTF-8 decode。

| # | URL | Response 類型 | 用途／實測 |
|---|---|---|---|
| U1 | <https://www.cnfin.com/> | `text/html` | 首頁、導覽、footer、公開文件／feed 線索 |
| U2 | <https://www.cnfin.com/news/index.html> | `text/html` | 伺服器回傳完整 HTML；首屏 13 個新聞項目 |
| U3 | <https://www.cnfin.com/robots.txt> | `text/plain` | crawler 規則及 sitemap 位置 |
| U4 | <https://www.cnfin.com/bqsm/index.html> | `text/html` | footer 連結嘅官方「版权声明」，頁內標題為「中国金融信息网版权与免责声明」 |
| U5 | <https://www.cnfin.com/sitemap/xml/sitemap-index.xml> | `text/xml` | robots 指向嘅 sitemap index；冇下載子 sitemap |
| U6 | <https://www.cnfin.com/static/js/cms-list.js> | `application/javascript` | 頁面實際引用嘅分頁實作；只讀，冇執行 |
| U7 | <https://www.cnfin.com/static/js/cjmn-index-list.js> | `application/javascript` | 列表初始化及 DOM 容器關係；只讀，冇執行 |
| U8 | <https://www.cnfin.com/in/newsdata/index_2.shtml> | `text/html` | 由 U2／U6 推導，再實際 GET 一次；回 HTML fragment，13 個項目 |

冇對 `http://`、其他 hostname、其他頁碼作 redirect 測試。U1–U8 呢次冇遇到登入牆、CAPTCHA、401／403／429；唔代表其他路徑或日後唔會有。遇到限制應停止，唔換身份、代理或繞過。

## 2. 使用條款、robots 同再分發

### confirmed

- **U3**：`User-agent: *` 對應空嘅 `Disallow`，並列出 U5；今次檔案冇列出 `Crawl-delay`。呢份 robots 冇禁止指定路徑，**唔係內容使用授權或 rate-limit 承諾**。
- **U4**：聲明將標示由中國金融信息網提供嘅作品版權歸於新華社，使用相關作品須先獲授權，已授權者仍須按授權範圍使用及註明來源。頁面亦區分轉載自其他媒體嘅作品，唔將轉載視為網站認同或保證內容。
- **U1／U2 footer** 同樣有版權保留及未經授權不得轉載嘅提示。

以上只係聲明嘅重點整理，唔代替原文或法律意見；實際使用前應重新查閱 U4。

### unknown／使用 gate

| 項目 | 狀態及影響 |
|---|---|
| 只保存標題、URL、日期等 metadata 是否獲准 | **unknown**；冇搵到專門授權 metadata extraction、cache 或再展示嘅公開條款。唔可以自行假定「只係 metadata」就豁免 |
| 自動 polling、保存期限、API／UI 展示、CSV export | **unknown**；要確認用途、用戶範圍、頻率、保留期及可否下載／再分發 |
| 第三方媒體原文權利 | **unknown**；CNFIN 轉載唔等於可以向本 PoC 再授權，亦唔可以一律將原作者／原始 publisher 寫成 CNFIN |
| 獨立使用條款／開放資料 license | 本次查閱範圍只定位到上述版權與免責聲明；其他適用條款是否存在仍 **unknown**，唔聲稱已遍查全站 |
| 公開 GitHub reference PoC | 程式碼 license 唔包含新聞數據權利。未確認前唔將真實標題、截圖、CSV、checkpoint 或頁面 fixture 放入 repo |

**啟用真實來源前需要確認授權依據。** 使用者同意技術方案本身，唔等於網站／權利人已授權。可經 U4 嘅官方聯絡途徑確認 metadata 使用、歸屬標示及再分發範圍；今次冇聯絡網站或取得授權。

## 3. 官方 RSS／API／developer documentation

有限查證包括 U1／U2 嘅 HTML `link`、導覽及 footer hyperlinks，並查閱 U3–U5：

| 候選接入方式 | 結果 |
|---|---|
| RSS／Atom | U1／U2 冇發現 feed discovery link；本次公開導覽冇定位到官方 RSS／Atom 文件。**unknown 是否另有 feed**，唔聲稱不存在 |
| 官方公開 news API／developer documentation | 冇定位到可引用嘅正式介面文件、API 使用條款、版本合約或 rate limit；**unknown** |
| 網頁內部 endpoint | U2 inline script 有 `cnfinApiHost` 指向 `api.cnfin.com` 嘅線索；**冇對呢個 host 發 API request**。網頁引用／browser network 可見都唔等於正式公開 API，更唔等於可供第三方使用 |
| Sitemap | U5 係標準 sitemap index，唔係 news RSS／API。實測有 45 個子 sitemap entry，所列最新 `lastmod` 為 `2025-02-24T20:53:35.000+08:00`；相對本次調查日期較舊，唔能夠證明最新新聞完整度或更新頻率 |
| 公開 HTML 列表 | **confirmed 技術可讀**，U2／U8 可以直接 GET 後抽 metadata；冇官方穩定 DOM／欄位合約，使用授權仍須確認 |

冇枚舉猜測 `/rss`、`/api` 等路徑，冇使用搜尋引擎作全網查找，亦冇遍查其他產品／公司網站。以上係有界限嘅「未找到」，唔係證明網站冇提供。若權利人提供正式 feed／API，同時確認使用權利，應優先用該介面，而唔係依賴網頁內部 endpoint。

## 4. 列表結構、metadata 同分頁

### 4.1 首屏唔依賴 JavaScript 先有資料

U2 嘅原始 HTTP body 已經包含新聞項目，唔係只有 JavaScript app shell：

| 觀察到嘅 selector／attribute | 用途 |
|---|---|
| `.ui-zxlist-wrap .cjmh-gdxw-cont` | 一般新聞列表容器 |
| `.ui-zxlist-item` | 每個新聞項目；U2、U8 各 13 個 |
| `.ui-zxlist-item h3 a` | 文字係標題，`href` 係文章連結 |
| `.ui-zxlist-item .ui-publish` | 顯示發布日期時間 |
| `.ui-zxlist-item .ui-sourceinfo` | 取樣值係「资讯 \| 要闻」；屬於 channel／category 路徑，**唔係 publisher** |
| `.ui-zxlist-wrap-top` | 獨立置頂容器；本次為空，非空置頂嘅資料結構／去重未驗證 |

U2／U8 各 13 個項目都有非空標題／URL 同日期時間；兩次取樣合共 26 個不同 URL，冇重疊。呢個只係一次觀察，**唔係固定 page size 或跨頁永不重複嘅保證**。

- 首屏取樣嘅文章 href 使用 protocol-relative URL，路徑形如 `/yw-lb/detail/YYYYMMDD/<numeric-id>_1.html`。可以取得候選數字 ID，但跨 channel 唯一性、重新發布、改 URL 後穩定性同 `_1` 語義都 **unknown**。
- U1／U2 冇 `rel=canonical` link；**冇開文章詳情，所以 article canonical URL 是否存在仍 unknown**。列表 href 只可視為 link target，唔可以宣稱已確認 canonical。
- 日期文字全部符合 `yyyy-MM-dd HH:mm:ss`，有年份及秒級**顯示精度**，但冇 offset、時區名稱或 `Z`。唔等於已知道發布時間嘅 UTC instant 或實際準確度。
- Sitemap 嘅 `+08:00` 只屬於 sitemap `lastmod`；script build comment 嘅時區亦唔係新聞時間合約。**兩者都唔可以用嚟確認 `.ui-publish` 嘅時區。**
- U1／U2 嘅 `<html>` 冇 `lang`；頁面文字以簡體中文為主，但唔代表每篇文章都有明確語言宣告。

### 4.2 分頁：HTML fragments，唔係已確認嘅公開 JSON API

**由 HTML／script 直接確認：**

1. U2 嘅 `.cjmh-gdxw li.cur` 帶 `data-url="/in/newsdata/index.shtml"`、`data-oneloadstatus="1"`，表示首屏已包含資料。
2. U7 用以上節點同 `.cjmh-gdxw-cont` 初始化 U6 嘅列表 loader。
3. U6 按頁碼將 `.shtml` 改成 `_<page>.shtml`，以 GET 取得 HTML 後 append；所以首屏之後嘅候選路徑為 U8。
4. U6 讀最後一個 `.listpage` 作頁數上限。U2／U8 嘅隱藏 `#listPage` 都係 `100`；**唔可以理解成每頁 100 筆或已驗證有 100 頁可讀**。
5. Script 有靠近列表底部時自動載入，之後顯示「点击加载更多」嘅邏輯；頁碼狀態少於 5 時走自動載入分支，後續由按鈕觸發。

**實際 network 驗證：** 只 GET U8 一次，回 200／UTF-8 HTML fragment，冇 `<html>` 根元素；仍有相同 item、title、category、time 結構。唔需要啟動 browser 或重播內部 API 先讀到呢個 fragment。

**未驗證：** browser scroll／click 嘅實際效果、第三頁起、末頁、頁數上限、置頂／改稿造成嘅重複／缺漏、頁碼漂移、排序一致性、歷史覆蓋範圍。U2 同 U8 唔係 atomic snapshot；首頁新增文章時，翻頁可能重複或漏資料。

## 5. Metadata mapping［proposal，未實作］

以下係日後 source-native DTO → mapper → typed news record 嘅候選規則，**唔係現有 API／checkpoint 欄位**。

| Internal metadata | 本次證據／可取得值 | 建議 mapping 同未知處理 |
|---|---|---|
| `ArticleId` | href 內有候選數字 ID；冇獨立 ID attribute 或官方穩定性合約 | **unknown**；唔將候選數字或本地衍生值當成 article ID。初稿 local identity 建議未採用，正式 identity／canonical 仍 blocked |
| `Title` | item 嘅 `h3 a` text | HTML decode、trim／整理空白，只保存純文字；唔將 HTML 注入 UI。空白或超限須明確記錄驗證錯誤 |
| `Url` | item anchor `href` | 相對 U2 解析成 absolute HTTPS URL；驗證 scheme／host，唔盲目跟外域、唔移除未知 query 後當成同一文章。冇 canonical 就保留已驗證 link target |
| `Publisher` | 可確認網站身份，但唔等於文章 publisher | **unknown**；唔由平台名稱、category 或 source 設定自行補 publisher |
| `OriginalPublisher` | 本次列表 item 冇原始媒體欄位 | `null`／unknown；唔由 category、標題前綴或平台名推斷，亦唔為填滿欄位而抓文章全文 |
| `Category` | `.ui-sourceinfo` 嘅 channel／category 文字 | 可保留「资讯／要闻」路徑，或者明確分開 channel 同 category；唔當 publisher。穩定 taxonomy ID 未確認 |
| Publication date/time | `.ui-publish` 文字 | 來源 DTO 保留原始時間；按固定格式解析成未指定時區嘅 local date/time，唔使用開發機時區 |
| Time precision | 取樣顯示至秒 | `second` 只表示字串精度；日後 date-only／缺年份資料須用不同 precision，唔自行補齊當成已知 |
| Timezone information | 欄位冇時區 | **unknown**，UTC instant 保持空值；唔套用開發機時區或 `Asia/Shanghai`／UTC+08:00 假設，須有來源時間合約先能確認 |
| `FetchedAtUtc` | 由本地取樣／ingestion 產生，唔係網頁欄位 | 用 `TimeProvider` 喺成功取得及驗證 response 時記錄 UTC；唔覆蓋 publication time |
| `Language` | HTML 冇 `lang`；文字樣本為簡體中文 | 保持 **unknown**；取樣觀察唔當逐篇語言宣告，唔自行填 source default |

唔保存摘要、正文、圖片、附件、sentiment 或 AI 摘要；source attribution 同使用授權亦唔係靠填滿 `Publisher` 就解決。

## 6. Parser 可行性同本機現況

### Parser 可行，但未達到正式 contract

- U2／U8 係可直接解析嘅 HTML；**唔需要另起 browser service 或 CNFIN microservice**。授權確認後可優先考慮 .NET `HttpClient` + HTML DOM parser，唔靠 regex 解析整頁。
- CSS selector 係本次可見結構，唔係永久 API。頁面有額外 wrapper，應限於列表容器／item 尋找欄位，唔假定 `ul` 直接子元素一定係 `li`。
- 本次只用標準 HTML parser／字串檢查做一次性取樣核對，**冇實作或測試 production .NET parser**。日後 automated tests 要用自製 synthetic HTML，唔放真實頁面 fixture，預設唔連 external website。
- 測試應包括 wrapper 變化、缺／空 title、重複 URL、相對 URL、外域／不安全 URL、無時區／無效日期、置頂重複、空列表同錯誤頁。
- 200 但唔符合列表結構，或者 items 全部驗證失敗，應回報 parse failure；唔當成「成功但冇新聞」，唔清空已保存資料。分辨真正空列表嘅規則目前仍需定義。
- HTTP timeout、body size cap、取消、限速同 parse error 都要明確記錄；未來 source 失敗只影響自己，唔阻止 mock sources 或 API。

### 已閱讀嘅現有接縫

| 現有程式碼 | confirmed 現況／對 news 嘅影響 |
|---|---|
| [ISourceAdapter](../backend/src/DataHub.Api/Sources/ISourceAdapter.cs)、[Models](../backend/src/DataHub.Api/Domain/Models.cs)、[MockSourceAdapters](../backend/src/DataHub.Api/Sources/Mock/MockSourceAdapters.cs) | snapshot／polling contract 明確使用 `PriceUpdate`、price revision 同完整價格 snapshot；**唔係通用 news adapter contract**。唔可以將標題等硬塞入 `PriceRecord` |
| [SourceRuntimeState](../backend/src/DataHub.Api/Sync/SourceRuntimeState.cs) | 未 sync 嘅恢復資料唔會變 `Live`，並按 feed 活動推導狀態。News 日後要保留 recovery 原則，但應以最近成功 fetch／parse 判斷連線健康，唔係有冇新文章 |
| [CheckpointModels](../backend/src/DataHub.Api/Persistence/CheckpointModels.cs)、[CheckpointSerializer](../backend/src/DataHub.Api/Persistence/CheckpointSerializer.cs) | 現有 schema version 1 係價格資料，拒絕不支援版本。加入 typed news 要另行處理 schema compatibility，今次冇改 |
| [DataHubEndpoints](../backend/src/DataHub.Api/Endpoints/DataHubEndpoints.cs) | 現有 read API 係 health／sources／prices，價格從 store 讀取；冇 news endpoint。日後 UI polling 只可讀 news store，唔可直接觸發 CNFIN request |

需要先有獨立 typed news model、bounded store、mock news 同 recovery 設計，再接外部來源；本階段唔建立呢啲功能，亦唔自動開始下一階段。列表只係有限窗口，唔可以將「唔喺本頁」理解成文章已刪除，更唔係現有價格 full snapshot 嘅同等保證。

## 7. 建議接入及 polling［proposal，未採用／未啟用］

1. **先處理使用權利**，優先向來源方確認有冇正式 RSS／API。未確認前 external source 維持 disabled，mock-only 路徑照常。
2. 若獲准用 HTML，優先以 .NET background polling 讀 U2 嘅 metadata；唔執行 tracking／廣告／第三方 script，唔下載文章全文。Source-native DTO 同內部 news record 之間要有 mapper。
3. 起步建議 **每 10 分鐘一次、每次只讀首屏、最多一個 in-flight request**，加入小幅 jitter 避免整點集中。呢個係保守工程起點，**唔係 CNFIN 公佈或批准嘅 polling interval**，今次冇跑定時 polling。
4. 首屏窗口會漏掉兩次 poll 之間被推出首頁嘅文章；唔保證完整新聞覆蓋。只有確認授權、rate limit 同覆蓋需求之後先決定有限翻頁策略，唔遍歷隱藏值所指嘅 100 頁。
5. 網絡／5xx 可用有限 exponential backoff；429 遵守 `Retry-After` 並暫停，唔即刻重試轟炸。401／403、CAPTCHA、登入要求等應停用來源並要求人工處理，唔換身份繞過。
6. ETag／Last-Modified conditional GET 可待確認 server 支援後使用；本次 U2 headers 冇提供呢兩個欄位，**未測 304 或其他頁面 cache 行為**。
7. API／UI 同 CSV（如日後加入）都只讀 bounded in-memory news store；外部請求唔跟 browser refresh 頻率走。保留最後已驗證資料並明確標示 stale／failure，恢復後完成新 fetch／parse 之前唔標 `Live`。

## 8. 結論、未確認事項同本次驗證

### 決定：調查完成；接入暫時 blocked

| Gate | 結果 |
|---|---|
| 公開 HTML metadata 技術讀取 | **confirmed 可行**：首頁／列表及一個後續 fragment 200，UTF-8、欄位結構可讀，唔依賴 JavaScript rendering |
| 正式 RSS／API contract | **unknown**：本次未定位到官方文件，唔使用內部 endpoint 冒充公開 API |
| 使用／保存／展示／再分發權利 | **blocked until confirmed**：未取得授權依據，版權聲明唔提供 metadata 自動化使用許可 |
| 時區、publisher、article ID／canonical、完整度 | **unknown**：使用者已確認保留未知值，唔推斷，唔聲稱完整覆蓋 |
| 立即實作 CNFIN adapter | **今次唔做**；先確認以上 gates 同完成獨立 news 基礎模型，之後另開實作階段 |

下一次決定接駁前，需要確認：

- 允許使用嘅場景：只限本機試驗，定包括 dashboard、API、CSV、分享 demo；相應授權依據、attribution 同保留期。
- 來源方能否補充 publisher／UTC publication instant／正式 ID／canonical 合約；未確認前一律 unknown，唔以本地推斷取代。
- 若日後需要更廣覆蓋，確認獲准嘅翻頁／正式 feed 同頻率；目前只有有限取樣，唔等於完整來源。

本次已執行 HTTP／encoding 核對、兩頁 metadata 結構及數量檢查、分頁 script 靜態閱讀、sitemap XML 解析、公開版權聲明及 bounded RSS／API 線索檢查。**未執行** browser scroll／click、production parser tests、長時間 polling、負載／限流測試、article 詳情查證或法律審核。呢次只改文件，冇重跑 backend／frontend build 或 tests；過往 PoC 測試結果唔視為今次 CNFIN 驗證。

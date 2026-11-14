# Trading Data Hub PoC（latest-state）

一個喺個人電腦獨立運行嘅 multi-source、multi-model Trading Data Hub PoC。
Equity latest-state prices 係第一個示範 data product，bounded recent news metadata cache 係第二個；
經 REST API 同 terminal-inspired 深色 UI 顯示，並以 checkpoint 恢復快取。

- 係 **latest-state Data Hub**，唔係歷史數據庫，亦唔係交易系統：冇 tick history、冇 audit trail、冇 order／position。
- **所有來源都係 mock**：唔連任何真實外部服務（Solace、MSCI、Barra、MSSQL 全部冇連）。
- 冇 Redis、PostgreSQL、Kafka 等額外基建：backend 用 in-memory store + 本機 checkpoint 檔案。
- Prices grid 支援 server-side sorting／filters、Pause／Resume、技術欄位 visibility 同有 row cap 嘅 request-time CSV export。
- News 使用獨立 schema、identity、publication time 同 retention；本階段只使用 synthetic mock，冇新聞 CSV。
- Data product、source/provider 同 transport 係不同概念；Solace 係 transport。Barra risk／factor exposures、PnL 同 alpha signals 只係未來方向，**未實作**。
- 技術設計、API 規格、恢復流程同限制嘅完整說明：[doc/technical-spec.md](doc/technical-spec.md)。

> 實際執行過邊啲驗證、邊啲**未**驗證，見 [§14](#14-驗證狀態)；已知限制見 [§15](#15-已知限制)。

## 目錄

1. [最短路徑](#1-最短路徑)
2. [架構一覽](#2-架構一覽)
3. [Folder structure](#3-folder-structure)
4. [環境要求](#4-環境要求)
5. [Backend](#5-backend)
6. [Frontend](#6-frontend)
7. [Docker 同 Docker Compose](#7-docker-同-docker-compose)
8. [Port 同環境變數](#8-port-同環境變數)
9. [Checkpoint 位置同 volume](#9-checkpoint-位置同-volume)
10. [Mock source 行為](#10-mock-source-行為)
11. [手動驗證流程（故障恢復）](#11-手動驗證流程故障恢復)
12. [API 速覽](#12-api-速覽)
13. [Extension points](#13-extension-points)
14. [驗證狀態](#14-驗證狀態)
15. [已知限制](#15-已知限制)

---

## 1. 最短路徑

### 方式 A：本機（兩個 terminal，無需 Docker）

```powershell
# Terminal 1：backend（http://localhost:5080）
cd backend
dotnet run --project src\DataHub.Api

# Terminal 2：frontend（http://localhost:5173）
cd frontend
npm install
npm run dev
```

開 <http://localhost:5173>。Backend 同 frontend 係兩個獨立程序，可以各自啟動、各自重啟。

### 方式 B：Docker Compose

```powershell
docker compose up --build -d
```

開 <http://localhost:8080>（nginx 提供 UI 並將 `/api` 轉發去 backend container）。停止：`docker compose down`。

> PowerShell 嘅 `curl` 係 `Invoke-WebRequest` 嘅 alias，下面一律寫 `curl.exe`。

---

## 2. 架構一覽

```text
 Mock sources (in-process)             Backend (ASP.NET Core, .NET 10)                              Browser
 ┌────────────────────┐   ┌───────────────────────────────────────────────────────────┐      ┌─────────────┐
 │ mock-stream-a  (S) │   │  SourceRunner x3 ──apply──▶ LatestStateStore (in-memory)   │      │ React 19 UI │
 │ mock-stream-b  (S) │──▶│  snapshot + stream/poll        key = (SourceId, InstrumentId)│─REST▶│ polling,    │
 │ mock-api-c     (P) │   │                                   │            ▲              │      │ 1-page grid │
 └────────────────────┘   │                      CheckpointService      RecoveryHostedService   └─────────────┘
   S = streaming            │                          │ periodic/manual/shutdown │ startup restore
   P = polling              │                          ▼                          │
                            │                  checkpoint.json (+ .bak) ──────────┘
                            └───────────────────────────────────────────────────────────┘
```

- 每個來源有自己嘅 `SourceRunner`：先攞一次完整 snapshot，之後用 streaming／polling 更新最新值。一個來源失敗只影響佢自己嘅 status。
- `LatestStateStore` 係 in-memory，key 係 `(SourceId, InstrumentId)`：**不同來源相同 InstrumentId 各自獨立，唔會互相覆蓋**（mock 三個來源都有 `MOCK00001`，價格同貨幣都唔同）。
- API 只讀 store（讀取唔使攞 lock），唔會即時問來源。
- `CheckpointService` 定時（預設 30 秒）、手動、同 shutdown 時將 store 寫入本機檔案；`RecoveryHostedService` 喺 backend 開始接受請求之前讀返出嚟。
- **恢復嘅舊數據唔會未同步就標示為 Live**：重啟後每個來源先係 `Recovering`，要該來源喺今次 process 完成 snapshot 先變 `Live`。
- Frontend 預設每輪完成後等 1 秒（可設定）；grid 只讀一頁（最多 200 行），health／sources monitoring 同 prices display 分開。只有使用者明確點擊 CSV export，先另外下載受 row cap 限制嘅 Blob。
- 獨立 `mock-news` 每 5 秒產生 synthetic metadata，經 native DTO → mapper → `NewsRecord` → API DTO；新聞唔使用 price snapshot、revision 或刪除語義。共用 header 只顯示 backend 連線狀態同時鐘；各產品來源狀態喺各自頁面顯示。
- NEWS SOURCES 面板顯示 fetch 狀態、last attempt／success、cache 數量、最新已知 publication instant 同錯誤。`Outage 20s` 只係模擬失敗；20 秒後或按 `End outage` 後，等下一次 fetch（最多約 5 秒）成功才恢復 Connected。舊文章時間唔代表來源斷線。
- News store key = `(SourceId, ArticleId)`，預設 `DataHub:News:MaxItems=500`、`RetentionDays=7`，以 first-seen 清理；有界 identity 記錄（最多 MaxItems）令已過期項目重複 fetch 唔會延長壽命，並保存到 checkpoint。大量新 identity 會淘汰最舊 identity 記錄；被遺忘後再次出現會視為新項目。列表缺席或 fetch 失敗唔刪除新聞。
- Checkpoint schema 2 保存兩種產品；schema 1 價格檔可讀，news 從空開始。未知未來 schema 會顯示 warning 並阻止保存，避免覆蓋；恢復新聞在本次成功 fetch 前唔標示 Connected。

---

## 3. Folder structure

```text
app-datahub-poc/
├─ README.md                        本文件
├─ doc/technical-spec.md            技術設計文件
├─ compose.yaml                     Docker Compose：backend + frontend 兩個 service
├─ compose.slow-snapshot.yaml       示範用 override：拉長 mock snapshot，肉眼睇到 RECOVERING
├─ .env.example                     Compose 變數範例（複製成 .env）
├─ .gitignore
├─ .github/                         copilot-instructions.md、prompts/build-datahub-poc.prompt.md
├─ .mcp.json                        （原本已存在嘅 Playwright MCP 設定，唔屬於 PoC）
├─ backend/
│  ├─ DataHub.sln · global.json · Directory.Build.props · Directory.Packages.props
│  ├─ Dockerfile · .dockerignore
│  ├─ src/DataHub.Api/              ASP.NET Core Web API（net10.0）
│  │  ├─ Program.cs · appsettings.json · appsettings.Development.json
│  │  ├─ Properties/launchSettings.json   F5／dotnet run 用嘅 profile（http://localhost:5080）
│  │  ├─ Configuration/             DataHubOptions、DataHubOptionsValidator、JSON 設定
│  │  ├─ Domain/                    PriceUpdate、PriceRecord、SourceStatus…
│  │  ├─ News/                      synthetic native DTO、mapper、typed store、fetch status、API
│  │  ├─ Sources/                   ISourceAdapter／ISourceCatalog；Mock/ 係 mock 實作
│  │  ├─ Store/                     ILatestStateStore、LatestStateStore（in-memory）、PriceQueryEngine
│  │  ├─ Sync/                      SourceRunner、SourceRegistry／SourceSupervisor、RecoveryHostedService、status 計算
│  │  ├─ Persistence/               Checkpoint 序列化、FileCheckpointStore、CheckpointService
│  │  ├─ Endpoints/                 Minimal API + DTO、PriceReadService、query validation、CSV
│  │  ├─ Hosting/                   DI 註冊、content root 判斷
│  │  └─ Serialization/             UTC 時間 JSON converter
│  └─ tests/DataHub.Api.Tests/      xUnit：Api/ Configuration/ Hosting/ News/ Persistence/ Sources/ Store/ Sync/ Support/
└─ frontend/
   ├─ package.json · vite.config.ts · .env.example · README.md
   ├─ Dockerfile · .dockerignore
   ├─ nginx/                        default.conf.template · security-headers.conf（只用喺 Docker image）
   └─ src/                          api/ · lib/ · hooks/ · components/ · styles/
```

`.dockerignore` 喺 `backend/` 同 `frontend/` 各有一份（兩個 Docker build context 就係呢兩個資料夾），所以根目錄冇 `.dockerignore`。

---

## 4. 環境要求

| 項目 | 要求 | 今次實際使用 |
|---|---|---|
| .NET SDK | 10（`backend/global.json` 要求 10.0.100，`rollForward: latestFeature`） | 10.0.403 |
| Node.js | 24 LTS（`frontend/.nvmrc`、`engines: >=24`、Docker image 都係 24） | 本機 **v26.12.0**；`node:24-alpine`（Docker／Linux container）內 24.22.2 |
| npm | 隨 Node 附帶 | 11.20.0（本機） |
| Docker | Linux containers（Docker Desktop + WSL2） | Docker 29.9.2 |
| Visual Studio | 要支援 .NET 10 SDK 嘅版本（官方資料顯示 10.0.4xx SDK 隨 Visual Studio 18.x／VS 2026 提供） | 本機已安裝 Visual Studio Community 2026（18.11）同 Build Tools 2026，但 **F5 未喺 IDE 內驗證** |

本機 Node 係 v26，唔係專案目標嘅 24 LTS：`npm install`／`npm test`／`npm run build`／`npm run lint` 喺 v26 跑過。Node 24.22.2 喺 `node:24-alpine`（Linux container）內跑過 `npm ci`、`npm run lint`、`npm test`、`npm run build`；**Windows 本機 Node 24 冇驗證**（見 §14）。

---

## 5. Backend

### 5.1 Visual Studio（F5）

1. 用支援 .NET 10 嘅 Visual Studio 開 `backend\DataHub.sln`。
2. 將 `DataHub.Api` 設為啟動專案。
3. Profile 揀 `DataHub.Api`（啟動後開 `/swagger`）或者 `DataHub.Api (no browser)`，然後 F5。
4. Backend 喺 `http://localhost:5080` 以 `Development` 環境運行。

> **未驗證**：呢部機雖然裝咗 Visual Studio Community 2026，但今次冇喺 IDE 入面操作，F5 流程冇親身跑過。F5 同 `dotnet run` 用同一份 `Properties\launchSettings.json`（profile 同 URL 一樣），而 `dotnet run` 已經驗證（見 §14）。
> Visual Studio 嘅「Stop Debugging」會點樣結束 process 亦冇驗證；想確保有 checkpoint，先撳 UI 嘅 **Save checkpoint**（見 §11）。

### 5.2 dotnet CLI

```powershell
cd backend
dotnet build DataHub.sln                       # warning 視為 error
dotnet test DataHub.sln                        # 206 個測試
dotnet run --project src\DataHub.Api           # DataHub.Api profile：Development、http://localhost:5080
```

- 檢查：`curl.exe http://localhost:5080/api/health`
- Swagger UI（只喺 Development）：<http://localhost:5080/swagger>；OpenAPI JSON：<http://localhost:5080/openapi/v1.json>
- 停止：**Ctrl+C**。Backend 會先停止同步，再寫最後一個 checkpoint（log：`Final checkpoint on shutdown: Saved.`）。
- 想由零開始（唔恢復）：停低 backend，刪除 `backend\src\DataHub.Api\data\` 再啟動。

### 5.3 發佈同執行（唔用 Docker，例如 Windows 主機）

```powershell
cd backend
dotnet publish src\DataHub.Api -c Release -o D:\apps\datahub\backend

# 之後喺目標機器（需要安裝 ASP.NET Core 10 runtime）：
$env:ASPNETCORE_URLS = 'http://localhost:5080'                         # 冇設定時預設都係呢個
$env:DataHub__Checkpoint__Directory = 'D:\apps\datahub\checkpoints'     # 建議用絕對路徑
dotnet D:\apps\datahub\backend\DataHub.Api.dll
```

- 已 publish 嘅 app 預設係 `Production` 環境：Swagger UI 關閉、冇 CORS。Frontend 要經 reverse proxy 將 `/api` 轉發去 backend（見 §6），或者設定 `DataHub__Api__CorsAllowedOrigins__0`。
- 由其他目錄（例如 Task Scheduler、服務、捷徑）啟動都得：如果工作目錄冇 `appsettings.json`，backend 會改用 DLL 所在資料夾做 content root（`Hosting/ContentRootResolver.cs`）。如果工作目錄**有**自己嘅 `appsettings.json`，就會用工作目錄（同 ASP.NET Core 預設一致，`dotnet run`／F5／Docker 都係咁）。
- `dotnet publish` 唔會帶走本機 `data\checkpoints\` 入面嘅執行期檔案（csproj 已排除）。
- 呢條路徑已經喺本機 Windows 開發機驗證過；**Windows Server 2022 本身、IIS／Windows Service 託管、Windows containers 都未驗證**。

---

## 6. Frontend

```powershell
cd frontend
npm install
npm run dev        # http://localhost:5173；/api 由 Vite proxy 轉發去 http://localhost:5080
npm run build      # tsc -b（型別檢查）+ vite build → dist/
npm run preview    # 用 production build 喺 http://localhost:4173 預覽（同樣有 /api proxy）
npm run lint       # oxlint
npm test           # vitest：poller、狀態 reducer、API client、table SSR 等，97 個測試
```

- Backend 未啟動時，UI 唔會白畫面：會顯示 **BACKEND DISCONNECTED**／**BACKEND UNREACHABLE** 並自動重試，backend 返嚟就自動接返。
- 設定見 [frontend/.env.example](frontend/.env.example)。`VITE_*` 係 **build 時**寫入 bundle，改咗要重新 build（Docker 要 `--build`）。
- Browser 一律打同源 `/api`（開發時由 Vite proxy、Docker 時由 nginx 轉發），所以唔需要 CORS。
- 需要 `AbortSignal.any`／`AbortSignal.timeout`：Chrome 116+、Firefox 124+、Safari 17.4+。
- 其他目錄同指令見 [frontend/README.md](frontend/README.md)。

### UI 概覽

| 區域 | 內容 |
|---|---|
| Header | `DATA HUB`、**MOCK** 標籤、backend 連線狀態（`BACKEND CONNECTED`／`DISCONNECTED`／`API ERROR`）、UTC 時鐘；各產品來源狀態喺各自頁面顯示 |
| Banners | `BACKEND UNREACHABLE`、`API ERROR`、`n OF m SOURCES NOT LIVE` |
| Toolbar | 來源 filter、instrument 搜尋（contains，300 ms debounce）、`Refresh snapshot`、`Save checkpoint` |
| Sources | 每個來源嘅 status、筆數、最後 snapshot／update、下次 snapshot、備註、`Outage 20s`（只限 mock） |
| Grid controls | Currency／source status／price range filters、`Reset filters/sort`、技術欄位顯示／隱藏、`Pause updates`／`Resume`、current-page／all-matching CSV export |
| Prices | 分頁表格（預設每頁 100，可揀 25/50/100/200）；六個 column header 可切 ascending／descending，附 indicator 同 `aria-sort`；價格變動閃一下並顯示 ▲／▼；↑↓／PageUp／PageDown／Home／End 移動，Enter／Space 選取，Esc 取消 |
| Status bar | 價格表最後更新、`DISPLAY PAUSED` 或 polling 間隔、總筆數、**CHECKPOINT**（`OK`／`NONE YET`／`SAVING…`／`ERROR`）、錯誤數 |

風格：只借鑑 Bloomberg Terminal 嘅資訊密度、深色配色同操作感；冇用 Bloomberg 商標、字型或任何專有素材。
閃爍代表「兩次 UI polling 之間嘅淨變動」，**唔係**每個 tick；每個狀態都有獨立形狀同文字，唔淨靠顏色；`prefers-reduced-motion` 時改為靜態標示。

**Grid 語義**：

- Filter → sort → paginate 全部喺 backend，唔係只排序 browser 收到嗰頁。Filter／sort／page-size 改變（包括切返原本條件）都返第 1 頁；搜尋保留 300 ms debounce。Reset 清除 filters／sort 同頁碼，唔改 page-size、column visibility 或 pause 狀態。
- `Columns` 可隱藏 SOURCE TIME、RECEIVED、REV，唔改 API 或 CSV 欄位。冇可靠 market metadata，所以冇 market filter。
- Pause 只停止價格表自動查詢；backend ingestion、health／sources monitoring 繼續，斷線照樣顯示 `BACKEND UNREACHABLE`。暫停時 filter／sort／page／page-size 改變仍查一次，唔會恢復定時刷新；Resume 即刻查詢，再按間隔刷新。
- CSV 係 **request-time data**，唔保證等於之前顯示或 paused 嘅畫面。上限、格式同 Excel 使用注意見 §12。

---

## 7. Docker 同 Docker Compose

```powershell
docker compose up --build -d      # 第一次會 build 兩個 image
docker compose ps                 # backend、frontend 都應該係 healthy
docker compose logs -f backend    # Ctrl+C 只係停止睇 log，唔會停 container
docker compose down               # 停止並移除 container，保留 checkpoint volume
docker compose down -v            # 連 checkpoint volume 一齊刪除（下次冇舊數據可恢復）
```

只有兩個 service，冇額外基建：

| Service | Image | 說明 |
|---|---|---|
| `backend` | `datahub-poc-backend:local` | `mcr.microsoft.com/dotnet/sdk:10.0` build → `aspnet:10.0` 運行；非 root（uid 1654 `app`）；`HEALTHCHECK` 打 `/api/health` |
| `frontend` | `datahub-poc-frontend:local` | `node:24-alpine` build → `nginxinc/nginx-unprivileged:stable-alpine`；非 root（nginx）；提供靜態 UI，將 `/api/` 轉發去 `http://backend:5080`；`HEALTHCHECK` 打 `/healthz` |

- Browser 只同 nginx（`:8080`）講嘢；`http://backend:5080` 係 Compose 內部網絡位址，browser 唔會用。所以唔需要 CORS。
- `frontend` 要等 `backend` healthy 先啟動。nginx 每 10 秒重新解析 `backend`，所以 backend container 重建（IP 變咗）之後唔使重啟 nginx。
- 兩個 service 都係 `restart: unless-stopped`；`backend` 嘅 `stop_grace_period` 係 30 秒，收到 SIGTERM 後會寫最後一個 checkpoint。
- nginx 加咗安全 header（含嚴格 CSP：`default-src 'self'`…）；`/assets/` 長期 cache，`index.html` 唔 cache，`/api/` 一律 `no-store`。
- 預設只綁 `127.0.0.1`。**Admin endpoints 冇認證**，唔好喺共用網絡將 `BIND_ADDRESS` 改成 `0.0.0.0`。
- 需要 **Linux containers**（Docker Desktop + WSL2）。Windows containers 冇測試；Windows Server 上 Docker Desktop 亦通常唔適用，呢種環境請用 §5.3 嘅非 Docker 路徑（**未喺 Windows Server 驗證**）。

Compose 變數用 `.env` 覆寫：`Copy-Item .env.example .env`，見 §8。

---

## 8. Port 同環境變數

### Port

| 用途 | 位址 | 備註 |
|---|---|---|
| Backend（`dotnet run`／F5） | `http://localhost:5080` | 來自 `launchSettings.json`；冇設定 URL 時 `Program.cs` 預設都係 5080 |
| Swagger UI | `http://localhost:5080/swagger` | 只喺 `Development`，或者 `DataHub__Api__SwaggerUiEnabled=true` |
| OpenAPI JSON | `http://localhost:5080/openapi/v1.json` | 所有環境 |
| Frontend dev（`npm run dev`） | `http://localhost:5173` | `strictPort`：被佔用會直接失敗，唔會自動轉 port |
| Frontend preview | `http://localhost:4173` | |
| Docker：frontend（nginx） | `http://localhost:8080` | host `127.0.0.1:8080` → container `8080` |
| Docker：backend | `http://localhost:5080` | host `127.0.0.1:5080` → container `5080` |

> **Windows 備註（單一機器嘅觀察）**：喺呢部 Windows 開發機，`curl.exe http://localhost:8080/…` 每次要約 2 秒，因為 `localhost` 會先試 IPv6 `::1`，而 Compose 只喺 IPv4 `127.0.0.1` 發佈 port；改用 `http://127.0.0.1:8080` 就係約 5–9 ms。Chromium 開 `http://localhost:8080` 冇觀察到呢個延遲。Vite dev／preview 預設 host 係 `localhost`，請用 `localhost` 存取（呢部機觀察到 preview 用 `127.0.0.1` 連唔到）。
### Backend 設定（`appsettings.json` 嘅 `DataHub` 區段）

任何設定都可以用環境變數覆寫：`:` 換成 `__`，例如 `DataHub:Checkpoint:Directory` → `DataHub__Checkpoint__Directory`；陣列用索引，例如 `DataHub__Api__CorsAllowedOrigins__0`。
設定喺啟動時驗證（`DataHubOptionsValidator`），無效值會令啟動失敗並列出所有問題（呢啲訊息係繁體中文；API 回應同 log 係英文）。

| 設定 | 預設 | 說明 |
|---|---|---|
| `UseMockSources` | `true` | 只支援 `true`；`false` 會令啟動失敗（見 §13） |
| `Snapshot:IntervalHours` | `168` | 完整 snapshot 間隔（每週） |
| `Snapshot:RetryInitialDelaySeconds` / `RetryMaxDelaySeconds` | `1` / `15` | 失敗後嘅 exponential backoff 起點同上限 |
| `Checkpoint:Enabled` | `true` | `false` = 完全唔寫、唔讀 checkpoint |
| `Checkpoint:Directory` | `data/checkpoints` | 相對路徑以 content root 為基準；可用絕對路徑 |
| `Checkpoint:FileName` | `checkpoint.json` | 只可以係檔案名 |
| `Checkpoint:IntervalSeconds` | `30` | 定時 checkpoint 間隔（資料冇變就跳過） |
| `Checkpoint:SaveOnShutdown` | `true` | 正常停止時寫最後一個 checkpoint |
| `Freshness:StaleAfterSeconds` | `30` | 冇任何 feed 活動超過呢個時間 → `Stale`；`0` 停用。Polling 來源至少用 3 倍 poll 間隔 |
| `Api:DefaultPageSize` / `MaxPageSize` | `100` / `500` | `/api/prices` 分頁 |
| `Api:ExportMaxRows` | `10000` | 每次 CSV 最多輸出行數，合法範圍 1–100000；超限回 422，唔截斷 |
| `Api:AdminEnabled` | `true` | `false` 會令 `/api/admin/*` 唔存在（UI 對應按鈕會停用） |
| `Api:SwaggerUiEnabled` | `false` | Development 一律開 |
| `Api:CorsAllowedOrigins` | `[]` | 空 = 唔啟用 CORS；Development 自動加 `http://localhost:5173`、`http://127.0.0.1:5173` |
| `Mock:Sources[n]:*` | 見 §10 | 每個 mock 來源嘅設定 |

### Backend 運行環境變數

| 變數 | 預設／例子 | 說明 |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development`（launchSettings）、`Production`（publish、Docker） | 影響 Swagger UI、CORS、錯誤處理 |
| `ASPNETCORE_URLS` | 冇設定 → `http://localhost:5080`；Docker：`http://+:5080` | Listen 位址 |

### Frontend 變數（`frontend/.env.example`，複製成 `.env.local`）

| 變數 | 預設 | 說明 |
|---|---|---|
| `VITE_POLL_INTERVAL_MS` | `1000` | UI polling 間隔（毫秒）。範圍 250–60000，超出範圍會被限制到邊界；唔係數字就用 1000。**Build 時**寫入 |
| `VITE_API_BASE_URL` | 空 | 空 = 同源 `/api`。填 `http://localhost:5080` 令 browser 直接打 backend（要配合 backend CORS）。**Build 時**寫入 |
| `DATAHUB_DEV_PROXY_TARGET` | `http://localhost:5080` | 只影響 `npm run dev`／`preview` 嘅 proxy 目標，唔會寫入 bundle |

### Docker Compose（`.env`，見 `.env.example`）

| 變數 | 預設 | 說明 |
|---|---|---|
| `BIND_ADDRESS` | `127.0.0.1` | Host 綁定位址 |
| `BACKEND_HOST_PORT` | `5080` | Backend 對外 port |
| `FRONTEND_HOST_PORT` | `8080` | Frontend 對外 port |
| `EXPORT_MAX_ROWS` | `10000` | Backend runtime `DataHub__Api__ExportMaxRows`；改咗要重建 backend container，唔使 rebuild image |
| `VITE_POLL_INTERVAL_MS` | `1000` | Frontend build arg，改咗要 `docker compose up --build` |

Container 內部（已喺 Dockerfile／`compose.yaml` 設定，一般唔使改）：backend `ASPNETCORE_URLS=http://+:5080`、`DataHub__Checkpoint__Directory=/var/lib/datahub/checkpoints`、`ASPNETCORE_ENVIRONMENT=Production`；frontend `BACKEND_UPSTREAM=http://backend:5080`。

**唔好將 credentials 放入任何設定檔或 `.env`**：呢個 PoC 完全唔需要 secret；`.env`／`.env.*`（`.env.example` 除外）已經喺 `.gitignore`。

---

## 9. Checkpoint 位置同 volume

| 運行方式 | Checkpoint 目錄 |
|---|---|
| `dotnet run`／Visual Studio F5 | `backend\src\DataHub.Api\data\checkpoints\`（content root = project 目錄；已 `.gitignore`） |
| 已 publish、由 publish 資料夾（或其他目錄）運行 | `<publish 資料夾>\data\checkpoints\`；建議用 `DataHub__Checkpoint__Directory` 指定絕對路徑 |
| Docker Compose | container 內 `/var/lib/datahub/checkpoints`，對應 named volume **`datahub-poc_datahub-checkpoints`** |

檔案：

| 檔案 | 用途 |
|---|---|
| `checkpoint.json` | 目前有效嘅 checkpoint（primary） |
| `checkpoint.json.bak` | 上一個完整驗證過嘅版本 |
| `checkpoint.json.tmp` | 寫入途中嘅暫存檔（成功後會取代 primary） |

- **寫入**：序列化 → 寫 `.tmp`（寫穿到磁碟）→ 讀返驗證長度同 SHA-256 → 原子取代 primary（舊 primary 變 `.bak`）。寫入失敗只會記錄錯誤，backend 繼續用 memory 數據服務。
- **讀取**：primary → `.bak` → 冇。檔案損毀只會變成警告（`/api/health` 嘅 `checkpoint.loadWarnings`），**永遠唔會阻止啟動**；冇可用 checkpoint 就由各來源 snapshot 開始。
- **內容**：每個來源一份 slice（record + 該來源嘅 revision/snapshot 時間）；全域一個 checkpoint 版本。唔包含 UI 狀態或歷史。
- **Docker**：volume 喺 container 重建後仍然保留，所以 `docker compose down` → `up`、`--force-recreate` 之後都恢復得到；`docker compose down -v` 先會刪除。
- 本機檔案系統（NTFS／Linux volume）通常可以保證原子取代；**網絡共享（SMB／UNC）唔保證**。

---

## 10. Mock source 行為

所有來源都喺 backend process 內模擬；預設三個 price sources（`Sources/Mock/`），另有一個 `mock-news`：

| SourceId | 類型 | Instruments | 更新節奏 | Snapshot 延遲 | 貨幣 | 小數位 |
|---|---|---|---|---|---|---|
| `mock-stream-a` | Streaming | 2000 | 每 250 ms 推 25 個更新 | 400 ms | USD | 4 |
| `mock-stream-b` | Streaming | 1500 | 每 400 ms 推 15 個更新 | 300 ms | HKD | 2 |
| `mock-api-c` | Polling（每 5 秒） | 800 | upstream 每 1000 ms 變 40 個 | 200 ms | EUR | 3 |

- **InstrumentId**：`MOCK00001`、`MOCK00002`…；三個來源用同一個編號空間，所以 `MOCK00001` 喺三個來源各有一行（價格同貨幣唔同），用嚟示範「同一 InstrumentId 唔會互相覆蓋」。
- **價格**：起始價 10–500（按來源 `Seed` 固定），之後逐個 tick 隨機漫步（每次最多 `MaxMovePercent`%）。每個 instrument 有來源自己嘅單調遞增 `revision`（**revision 只喺同一來源內可比較**）。
- **Streaming**：先訂閱、再攞 snapshot；斷線／訂閱隊列滿（1024 個 batch）→ 自動重新訂閱，並即刻再攞一個 snapshot 補返可能漏咗嘅更新（期間 `Recovering`）。
- **Polling**：只攞 `revision` 比上次 cursor 新嘅 record；同一 instrument 兩次 poll 之間變咗幾次，只見到最新一次。
- **新上市／下市**：每個更新位有極細機率（`MembershipChangeProbability`，`appsettings.json` 設為 `0.0002`）係上市或下市，所以筆數會慢慢漂移（例如 2000 → 2001）。新上市會經 stream／poll 通知；**下市唔會通知**，要等下一次完整 snapshot 先會移除（撳 `Refresh snapshot` 可即時觸發）。
- **Snapshot 延遲**：模擬傳輸時間。先「切」snapshot，之後先等；等待期間 upstream 繼續變動，用嚟重現 snapshot 同新更新嘅競爭（Hub 以 revision 比較，唔會被舊 snapshot 蓋走新值）。
- **模擬中斷**：UI 嘅 `Outage 20s` 或 `POST /api/admin/mock/{sourceId}/outage?seconds=20`。期間 stream 斷線、snapshot／poll 失敗 → `Disconnected`（備註 `ERR: Simulated outage active until …`）；結束後自動重連、重新 snapshot，`Recovering` → `Live`。`seconds=0` 即時結束。

---

## 11. 手動驗證流程（故障恢復）

先決條件：backend 同 frontend 按 §1 方式 A 分別啟動。

| 步驟 | 動作 | 預期 |
|---|---|---|
| 1 | 分別啟動 backend（Terminal 1）同 frontend（Terminal 2） | Backend log 見 `Starting 3 source runner(s)`；UI 開得到 |
| 2 | 觀察 snapshot 數據同持續更新 | EQUITY PRICE SOURCES 面板顯示三個來源 `LIVE`，筆數約 2000／1500／800（共約 4300 行）；價格格仔隨 polling 閃動 ▲／▼；`curl.exe http://localhost:5080/api/sources` 見 3 個 `Live` |
| 3 | 手動保存 checkpoint：撳 **Save checkpoint**（或 `curl.exe -X POST http://localhost:5080/api/admin/checkpoint`） | Toolbar 顯示 `OK`；status bar **CHECKPOINT OK**（時間 · 筆數 · 大小 · 耗時）；`backend\src\DataHub.Api\data\checkpoints\checkpoint.json` 出現 |
| 4 | **停止 backend**（Terminal 1 撳 Ctrl+C），frontend 繼續運行 | Backend log：`Final checkpoint on shutdown: Saved.` |
| 5 | 睇 UI | 紅色 **BACKEND UNREACHABLE** banner、Header `BACKEND DISCONNECTED`；表格保留最後數據但變暗；每個來源同價格行嘅 status 改顯示 `DISCONNECTED`（滑鼠移上去嘅提示會寫 `Last known: LIVE`，並註明未能確認）；admin 按鈕停用；UI 持續重試 |
| 6 | 重新啟動 backend：`dotnet run --project src\DataHub.Api` | Log：`Restored N record(s) from primary checkpoint saved at …` |
| 7 | 觀察 checkpoint recovery 同各來源重新同步 | UI 自動重連；`curl.exe http://localhost:5080/api/health` 嘅 `checkpoint.loadedFrom` 係 `"primary"`；每個來源先係 **RECOVERING**（備註 `RESTORED, AWAITING SNAPSHOT`），完成今次 snapshot 後先變 **LIVE**（備註 `RESTORED → RESYNCED`）；status bar 顯示 `restored at startup` |
| 8 | 用 Docker Compose 重做一次，驗證 container 重建後資料仍然存在 | 見下面「Docker 版本」 |

### 睇到 RECOVERING 中間狀態

預設 mock snapshot 只需 0.2–0.4 秒，恢復後差唔多即刻 LIVE。拉長 snapshot 時間就睇得清楚（`mock-stream-a` 約 0.4 秒、`mock-api-c` 約 6 秒、`mock-stream-b` 約 12 秒先 LIVE）：

```powershell
# 步驟 6 之前，喺 Terminal 1 設定（只影響呢個 terminal）
$env:DataHub__Mock__Sources__1__SnapshotLatencyMs = '12000'   # mock-stream-b
$env:DataHub__Mock__Sources__2__SnapshotLatencyMs = '6000'    # mock-api-c
dotnet run --project src\DataHub.Api

# 示範完，清走：
Remove-Item Env:DataHub__Mock__Sources__1__SnapshotLatencyMs, Env:DataHub__Mock__Sources__2__SnapshotLatencyMs
```

### 模擬 crash（冇機會寫 shutdown checkpoint）

Ctrl+C 係正常停止，會寫最後一個 checkpoint。強制結束 process（例如 `Stop-Process -Id <PID> -Force`）就冇：恢復到嘅係**最近一次定時 checkpoint**（預設每 30 秒；資料冇變就唔寫），之後嘅變動會喺重啟後由 snapshot 補返。

### Docker 版本（步驟 8）

```powershell
docker compose up --build -d                  # 開 http://localhost:8080，等三個來源 LIVE
# 撳 Save checkpoint（或：curl.exe -X POST http://localhost:5080/api/admin/checkpoint）

# (a) 停低再起：container 被移除，volume 保留
docker compose down
docker compose up -d
docker compose logs backend | Select-String "Restored"     # Restored N record(s) from primary checkpoint ...

# (b) 重建 backend container（想睇 RECOVERING：加 slow-snapshot override）
docker compose -f compose.yaml -f compose.slow-snapshot.yaml up -d --force-recreate backend
# UI 先顯示 BACKEND UNREACHABLE，backend 返嚟後三個來源先後 RECOVERING → LIVE；nginx 唔使重啟
docker compose up -d --force-recreate backend                # 回復預設設定

# (c) 模擬 crash：docker kill 唔會觸發 restart policy（Docker 視為手動停止），要自己起返
docker compose kill backend
docker compose up -d                                         # 恢復到最近一次定時 checkpoint

# (d) 對照：連 volume 一齊刪除，就冇舊數據可恢復
docker compose down -v
```

---

## 12. API 速覽

JSON 用 camelCase；enum 係 PascalCase 字串；時間一律 UTC（`…Z`）；`price` 係 JSON number。

| Method | Path | 說明 |
|---|---|---|
| GET | `/api/health` | Process 存活（永遠 200）+ 來源狀態摘要 + checkpoint 狀態（`loadedFrom`、`lastSuccessAtUtc`、`lastError`…） |
| GET | `/api/sources` | 每個來源嘅 `status`、`recordCount`、`syncedSinceStart`、最後 snapshot／update 時間、`lastError` |
| GET | `/api/prices` | Filter／sort 後分頁；`page` 預設 1，`pageSize` 預設 100、上限預設 500。每行帶 `sourceStatus`，回應帶符合來源／status filter 嘅來源 freshness |
| GET | `/api/prices/export.csv` | 同一套 filter／sort，`scope=page`（預設）或 `scope=all`；超出 `Api:ExportMaxRows` → 422 `problem+json` |
| GET | `/api/prices/{sourceId}/{instrumentId}` | 單一 record；未知來源或 instrument → 404 |
| GET | `/api/news` | Bounded news cache；`page`、`pageSize`、`keyword`、`category`、`publishedFrom`、`publishedTo`、`sortDirection`；回應包括 news source fetch 狀態 |
| POST | `/api/admin/snapshot/{sourceId}` | 即時重新攞 snapshot：200／404／409（已有 snapshot 運行中）／502（來源失敗） |
| POST | `/api/admin/checkpoint` | 即時寫 checkpoint：200／409（忙緊、停用或冇資料）／500（寫入失敗） |
| POST | `/api/admin/mock/{sourceId}/outage?seconds=N` | 模擬 mock 來源中斷（預設 20，範圍 0–3600，0 = 結束）；非 mock 來源 → 409 |
| POST | `/api/admin/mock-news/outage?seconds=N` | 只模擬 synthetic news fetch 中斷，唔影響 prices |

錯誤一律係 `application/problem+json`。`/api/admin/*` **冇認證**（PoC）；`DataHub__Api__AdminEnabled=false` 可以完全關閉。

News keyword 係標題 contains、category 係不分大小寫精確匹配；日期使用 `yyyy-MM-dd` inclusive，
instant 以 UTC 日期比較，date-only 保留原日期，未知日期不匹配日期範圍。Publication sort 預設 `desc`，
未知時間兩個方向均排最後，同日按已知 instant 排序、再以 source／article ID 升序 tie-break；
date-only 不會假裝成確切 instant。跨頁請求唔保證同一個 snapshot。

```powershell
curl.exe http://localhost:5080/api/health
curl.exe "http://localhost:5080/api/prices?instrumentId=MOCK00001&pageSize=10"
curl.exe "http://localhost:5080/api/prices?currency=EUR&sourceStatus=Live&minPrice=0&maxPrice=500&sortBy=price&sortDirection=desc&pageSize=25"
curl.exe "http://localhost:5080/api/prices/export.csv?scope=all&instrumentId=MOCK00001&sortBy=price" --output prices.csv
curl.exe -X POST http://localhost:5080/api/admin/snapshot/mock-api-c
```

欄位、狀態定義同範例見 [doc/technical-spec.md §8](doc/technical-spec.md)。

### Prices query 同 CSV

| 參數 | 語義 |
|---|---|
| `sourceId`／`instrumentId` | 保留精確來源 filter（不分大小寫，未知來源 404）／contains 搜尋（不分大小寫，最長 64 字元） |
| `sortBy` | Allowlist：`sourceId`（預設）、`instrumentId`、`price`、`currency`、`sourceUpdatedAtUtc`、`receivedAtUtc` |
| `sortDirection` | `asc`（預設）／`desc`；sort field／direction 名稱忽略大小寫 |
| `currency` | 三個 ASCII 英文字母，忽略大小寫嘅精確比對 |
| `sourceStatus` | `Initializing`／`Live`／`Recovering`／`Stale`／`Disconnected`，忽略大小寫，唔接受 enum 數字 |
| `minPrice`／`maxPrice` | Inclusive decimal bounds；`.` 小數點、可帶正負號，唔接受千位分隔、exponent 或 min > max |

文字 ordinal 排序，數字／時間按本身型別；null 兩個方向都排最後（現有價格／時間欄位不可為 null）。最後一律以 `SourceId` + `InstrumentId` **ascending ordinal** tie-break。Table、count 同 CSV 共用 captured per-source view 同查詢引擎；每個請求只取一次狀態評估時間。跨來源仍然係 fuzzy view，**唔係全域 snapshot，亦唔保證不同頁／不同 export request 一致**；按價格或時間排序時，live update 可以令行跳頁。

CSV 注意事項：

- `page` 按目前 query／page 重新查詢；`all` 保留 filter／sort，完全忽略 `page`／`pageSize`（即使帶入無效分頁值）。上限計**輸出行數**，超限明確拒絕，唔會靜默截斷。
- UTF-8 BOM、CRLF、文字加雙引號並 escape 雙引號；UTC ISO 8601 時間、invariant decimal／revision。Backend `Content-Disposition` 用安全 UTC timestamp filename，回應 `Cache-Control: no-store`；UI 另用固定安全名稱 `prices-page.csv`／`prices-all.csv`。
- 文字去除前置空白後以 `= + - @` 起首，或包含 control／Unicode Format 字元，就加前置 apostrophe `'`。**表示取捨**：下載文字可能多一個 `'`，普通換行文字亦會受影響；呢個唔係逐字還原格式。正常負數價格仍然係數字，唔加前綴。
- 唔用 `="ticker"` 包裝 ticker。**Excel 匯入 INSTRUMENT 時請選文字型別**，避免前導零被轉成數字；冇用 desktop Excel 驗證顯示。
- CSV 包含全部八個 backend 欄位，唔受 UI 隱藏欄影響，亦唔含 client-only `CHG`。Browser 唔逐頁拼接，只下載一個有 row cap 嘅 Blob。
- 使用 immutable per-source references，序列化期間唔重讀 store，支援 request cancellation，唔持有 ingestion lock。Sorting 需要 O(M log M) 時間／O(M) matching references；**row cap 唔等於所有 query 工作記憶體上限**，current-page export 仍可能排序更多行。未做大資料量／負載測試。
- 唔好將下載嘅 CSV、runtime checkpoint 或第三方數據放入 repo。

---

## 13. Extension points

> 以下係設計上嘅擴充點。**今次只實作 mock，下面嘅真實來源／MSSQL 全部未實作、未驗證。**

CNFIN news 嘅獨立調查見 [doc/cnfin-discovery.md](doc/cnfin-discovery.md)（2026-11-06）。使用者已接受 Prompt 1；CNFIN 維持 **blocked／未啟用**，未確認 metadata 使用授權前唔正式擷取或 polling。時區、publisher、article ID／canonical URL 保持 unknown，唔自行推斷；有限取樣唔代表完整來源覆蓋。**未實作 adapter，runtime 仍然 mock-only**。下面嘅 adapter 擴充點係價格合約，唔可以直接套用到 news。

| 目標 | 擴充點 | 要做嘅事 |
|---|---|---|
| 真實 Solace（streaming） | `IStreamingSourceAdapter` | `GetSnapshotAsync` 攞完整 snapshot；`SubscribeAsync` 回傳 `IUpdateSubscription`（update batch、空 batch = heartbeat；斷線就拋例外或完成，Hub 會重新訂閱並 resync） |
| External REST API（polling） | `IPollingSourceAdapter` | `GetSnapshotAsync` + `PollAsync(afterRevision)`；polling 睇唔到刪除，刪除靠下一次 snapshot |
| 註冊新來源 | `ISourceCatalog`（`Sources/ISourceCatalog.cs`） | 實作 `ISourceCatalog.Adapters` 並喺 `AddDataHub` 之後註冊，取代預設 `MockSourceCatalog`；store、runner、status、checkpoint、API 唔使改 |
| MSSQL 持久化 | `ICheckpointStore`（`LoadAsync`／`SaveAsync`） | 新增一個實作取代 `FileCheckpointStore`；`CheckpointService` 嘅排程、版本、失敗處理唔使改 |

要留意：

- 而家 `UseMockSources=false` 會令啟動失敗（刻意防止誤以為已連真實來源）；接真實來源時要一併調整 `DataHubOptionsValidator`（以及 `Mock:Sources` 至少一個嘅檢查）。
- Revision 只喺同一來源內可比較；真實來源嘅 snapshot 一致性（snapshot 同 stream 序號嘅關係）、排序保證、刪除通知能力都**未確認**，接駁前要同來源方確認，否則 [doc/technical-spec.md §6.6](doc/technical-spec.md) 講嘅「先訂閱、再攞 snapshot」策略可能要調整。完整嘅接駁步驟、要確認嘅問題清單、MSSQL 持久化同 production 前缺口見 [doc/technical-spec.md §12](doc/technical-spec.md)。
- 真實來源需要 credentials 時，用環境變數或 secret store 提供，**唔好提交落 repo**。

---

## 14. 驗證狀態

以下舊記錄係先前文件記載，唔代表新電腦或今次修改已重新執行。今次實際驗證另見本次交付摘要；
Node 24、Docker、Visual Studio F5 同舊 browser 驗證不可推論為今次通過。

### Prompt 3（本機，2026-11-15）

- .NET SDK 10.0.404：`dotnet build backend\DataHub.sln --no-restore` 成功，0 warning／error；`dotnet test backend\DataHub.sln --no-build --no-restore` 218／218 通過。
- Windows Node v26.13.0／npm 11.20.0：在 `frontend` 執行 `npm run lint`、`npm test`、`npm run build`，lint 同 build 成功，12 files／101 tests 通過。
- 獨立啟動 backend（checkpoint 停用）及 Vite，Playwright 檢查 EQUITIES／NEWS 切換、keyword、details、unknown/date-only 顯示；模擬 news outage 時 UI 顯示 disconnected 而 cache 保留，price sources 仍 3／3 live。停止測試服務後 browser polling 產生預期 connection-refused 訊息；未測完整無障礙、其他瀏覽器或新版本 Docker／F5。

### Prompt 2：grid 升級（2026-11-06）

| 項目 | 實際驗證 | 結果 |
|---|---|---|
| Backend | `dotnet build backend\DataHub.sln --no-restore`；`dotnet test backend\DataHub.sln --no-build --filter "FullyQualifiedName~.Api.\|FullyQualifiedName~.Store.\|FullyQualifiedName~.Configuration."` | build 0 warning／0 error；**206／206 通過**（namespace 令 selector 實際涵蓋全部測試） |
| Frontend，本機 Node v26 | `npm run lint`、`npm test`、`npm run build` | lint exit 0；**97／97、10 個 files 通過**；type-check／build 成功；頁碼來回切換修正後已重跑 |
| Frontend，Node 24.22.2 | Docker build stage 執行 build，再喺該 stage 跑 lint／test | 0 warning／0 error；**97／97 通過**；最後 frontend image 已重建 |
| Docker Compose | 預設同 slow-snapshot `config --quiet`；兩個 image build；獨立測試 project 啟動 | 兩個 container healthy；nginx CSV 200、BOM／attachment／no-store 正確；`EXPORT_MAX_ROWS=2` 時 3 行 all-matching 回 422 `problem+json` |
| Chromium grid | 獨立 `dotnet run` + `npm run dev`，Playwright 操作 | 升降排序、組合 filters／invalid range、reset、隱藏技術欄、鍵盤、搜尋三來源同名 record、640 px／reduced motion 通過 |
| Pause／recovery | 暫停、改 sort、停止及重啟 backend、slow snapshot、Resume | 暫停後價格唔自動查詢但 monitoring 繼續；改 sort 只查一次；斷線保留行並顯示 `DISCONNECTED`；恢復舊行先係 `RECOVERING`；Resume 即時及定時查詢 |
| CSV UI | 測試 cap=200；current-page／all-matching 下載及超限提示 | 25 行 page CSV、3 行 filtered all CSV、UTF-8 BOM 正確；超限顯示錯誤而唔下載；暫停狀態冇被 export 改變 |
| 頁碼 regression | 先重現「切返原本 filter 復活第 2 頁」，修正後重跑 | source／sort／page-size／currency／status／price bound／search 來回切換都返第 1 頁；舊選取亦唔會復活 |

Browser console 只有刻意觸發嘅 400／422 同停機期間嘅 502 resource errors，冇觀察到 JS 例外。第一次 browser probe 嘅 selector／resource-timing buffer timeout 已修正檢查方式並重跑；唔將失敗 probe 當成功。暫存 CSV、checkpoint、截圖同測試服務喺驗證後清理。以下初版 PoC 記錄係歷史結果，**唔表示 Prompt 2 重跑過其中每項操作**。

#### Prompt 2 修改檔案

以下路徑以各組目錄為基準；冇修改 ingestion adapters／checkpoint schema，冇新增依賴。

| 目錄 | 新增 | 修改 |
|---|---|---|
| `backend/src/DataHub.Api/Store` | `PriceQueryEngine.cs` | `StoreModels.cs`、`LatestStateStore.cs` |
| `backend/src/DataHub.Api/Endpoints` | `PriceQueryParameters.cs`、`PriceReadService.cs`、`PricesCsv.cs` | `DataHubEndpoints.cs`、`Dtos.cs` |
| `backend/src/DataHub.Api` | — | `Configuration/DataHubOptions.cs`、`Configuration/DataHubOptionsValidator.cs`、`Sync/SourceRegistry.cs`、`Hosting/ServiceCollectionExtensions.cs`、`appsettings.json` |
| `backend/tests/DataHub.Api.Tests` | `Store/PriceQueryEngineTests.cs`、`Api/PriceGridApiTests.cs`、`Api/PricesCsvTests.cs` | `Configuration/DataHubOptionsValidatorTests.cs` |
| `frontend/src` | `lib/grid.ts`、`lib/pricePoller.ts`、`lib/pricePoller.test.ts`、`components/GridControls.tsx`、`components/PricesTable.test.tsx` | `App.tsx`、`api/client.ts`、`api/client.test.ts`、`lib/dataHubState.ts`、`lib/dataHubState.test.ts`、`hooks/useDataHub.ts`、`components/PricesTable.tsx`、`components/StatusBar.tsx`、`styles/app.css` |
| `frontend` | — | `vite.config.ts`、`README.md` |
| 根目錄／文件 | — | `compose.yaml`、`.env.example`、`README.md`、`doc/technical-spec.md`、`doc/cnfin-discovery.md` |

Dockerfiles 無需修改：現有 build context／COPY 已包括新 source，兩個 image 已成功 build；Compose 同 example 設定已接上 export cap。

### 初版 PoC 已執行記錄（歷史）

| 項目 | 指令／做法 | 結果 |
|---|---|---|
| Backend build | `dotnet build DataHub.sln --no-incremental` | 成功，0 warning、0 error |
| Backend tests | `dotnet test DataHub.sln --no-build` | **166／166 通過**（涵蓋見下） |
| Frontend（Windows 本機 Node v26.12.0） | `npm run lint`、`npm test`、`npm run build` | lint exit 0；8 個 test file／**78 個測試通過**；build 成功 |
| Frontend（Node 24.22.2） | 喺 `node:24-alpine` container（Linux）入面 `npm ci`，再跑 `npm run lint`、`npm test`、`npm run build` | lint 0 warning／0 error；**78／78 通過**；build 成功，bundle hash 同本機一樣。呢個係 Linux container，唔係 Windows 本機 Node 24 |
| Frontend preview | `npm run preview`（backend 同時喺 `:5080` 運行） | `http://localhost:4173/` 回 200；`/api/health`、`/api/sources` 經 proxy 回到 backend 實際數據 |
| Publish + 非 Docker 運行 | `dotnet publish -c Release`，再由另一個工作目錄運行 publish 出嚟嘅 DLL（`ASPNETCORE_URLS=http://127.0.0.1:5090`） | 三個來源 `Live`（約 2000／1500／800）；手動 checkpoint 存到 `<publish>\data\checkpoints`，冇喺該工作目錄建立 `data`；`/swagger/index.html` 404、`/openapi/v1.json` 200；publish 輸出唔含本機 `data\` |
| Windows 正常停止 | `dotnet run` 運行中對 console 發 Ctrl+C | 30 秒內結束；log 有 `Final checkpoint on shutdown: Saved.`，checkpoint 檔案時間更新（驗證 script 冇攞到 exit code） |
| Docker | `docker compose config --quiet`（`compose.yaml`，以及加 `compose.slow-snapshot.yaml`）、`build`／`up`／`down`、healthcheck、`stop`、`--force-recreate`、`kill` | 兩個 container 都 healthy；見下 |
| UI 手動驗證 | Docker nginx UI（`http://localhost:8080`），用 Playwright（Chromium）操作 | 見下 |

Backend 測試涵蓋：store（per-source 隔離、revision 比較、snapshot merge／replace、移除與新增）、`SourceRunner`（streaming／polling／重連／resync／backoff／stale，用 `FakeTimeProvider` 驅動，唔靠長 sleep）、status 評估、checkpoint（序列化、原子寫入、備份回退、損毀處理、重疊保存）、恢復（恢復數據唔會係 `Live`）、API（驗證、分頁、錯誤、admin）、設定驗證、content root 判斷、mock upstream。

Docker 實測內容（單次執行）：

- 兩個 container healthy，frontend 等 backend healthy 先起。
- nginx：安全 header（含 CSP）、`index.html` 用 `no-cache`、hash 資源 `immutable` + gzip、SPA fallback、未知 `/api/*` 回 404、`/api/prices?page=0` 嘅 400 `problem+json` 透傳。
- (a) `down` → `up -d`：log 見 `Restored 4302 record(s) from primary checkpoint`，`checkpoint.loadedFrom` 係 `primary`，3／3 `Live`。
- (b) 用 slow-snapshot override `--force-recreate backend`，每 250 ms poll：三個來源先全部 `Recovering`，`mock-stream-a` 約 0.8 秒 `Live`、`mock-api-c` 約 6.4 秒、`mock-stream-b` 約 12.4 秒；之後改返預設設定。
- (c) `docker compose kill backend`：`Exited (137)`，restart policy 冇起返佢，冇 shutdown checkpoint 嘅 log；`up -d` 後恢復到最近一次定時 checkpoint（4300 筆）再 3／3 `Live`。
- (d) `down -v` → `up -d`：冇 `Restored` log、`loadedFrom` 係空，3／3 `Live`。
- `docker compose stop`（SIGTERM）約 1 秒、exit 0，log 有 `Final checkpoint on shutdown: Saved.`；再 `start` 後約 0.8 秒返 `Live`。
- Backend 停止期間 nginx `/healthz` 仍然 200，`/api/*` 回 502（connection refused）或 504（`proxy_connect_timeout 3s`）。

初版 UI 實測內容（Docker nginx UI，Chromium；初版交付時對住當時 image 重新跑齊）：

- 載入：第一頁 100 行、Header `LIVE 3/3`、冇預設選取。
- 鍵盤：Enter／Space 選取、↑↓、PageUp／PageDown、Home／End、Esc 取消選取；roving tab stop 跟住選取。
- 來源 filter（下拉，同點擊 SOURCES 面板嘅來源、再點一次取消）、instrument 搜尋（`MOCK00001` 出 3 行：三個來源各一行，幣別同小數位唔同，冇互相覆蓋）、empty state（`ZZZZ-NOPE`）。
- 分頁：每頁 25 行、下一頁、最後一頁、第一頁，邊界位置嘅 Previous／Next 停用。
- Toolbar：`Refresh snapshot`（全部同單一來源）、`Save checkpoint`。
- Mock outage：`mock-stream-b` 約 0.3 秒轉 `DISCONNECTED`，其餘兩個維持 `LIVE`；約 30 秒後返 `LIVE`。
- `prefers-reduced-motion: reduce` 下閃爍 animation 係 `none`（保留靜態顏色）；一般設定下係 `flash-up`／`flash-down`（0.9 秒）。
- 窄視窗（640 px）：頁面冇橫向 overflow，價格表喺自己嘅容器內橫向捲動。
- 停止 backend container（`docker compose stop`）：`BACKEND UNREACHABLE` banner、Header `BACKEND DISCONNECTED`、來源同價格行改顯示 `DISCONNECTED`（提示 `Last known: LIVE…`）、舊數據（100 行）保留、toolbar 嘅 admin 按鈕停用並提示 `Backend is unreachable.`（每個來源嘅 Outage 按鈕亦停用）；`docker compose start backend` 後自動接返，`LIVE 3/3`。
- 故障恢復：slow-snapshot override 下 `--force-recreate backend`，UI 見 `2 OF 3 SOURCES NOT LIVE` banner、`RECOVERING` badge、備註 `SNAPSHOT RUNNING`／`RESTORED, AWAITING SNAPSHOT`；篩選到 `mock-stream-b` 時，同步完成之前嗰 100 行嘅 status 全部係 `RECOVERING`，完成後先變 `LIVE`，備註 `RESTORED → RESYNCED`；status bar 顯示 `restored at startup`。
- Console：冇 JS 錯誤、冇 CSP 錯誤；只有 backend 被刻意停止／重建期間 browser 自己記錄嘅 502／504 `Failed to load resource`。

### 未驗證

- **Visual Studio F5／Stop Debugging**：本機有 Visual Studio Community 2026，但冇喺 IDE 入面跑過；只驗證咗 `dotnet run`。
- **Windows Server 2022**、IIS／Windows Service 託管、**Windows containers**；Docker 只用 Linux containers 驗證。
- **Windows 本機 Node 24**：本機係 v26.12.0；Node 24.22.2 只喺 Linux container（`node:24-alpine`）內跑過 lint／test／build。
- **其他瀏覽器**：只用 Chromium 驗證，Firefox／Safari 冇測。
- **Desktop Excel 匯入同大型 export 壓力測試**：未執行；CSV bytes／escaping／formula 防護由測試驗證，唔等於 Excel 各版本顯示保證。
- **ErrorBoundary** 嘅「UI ERROR」畫面，以及分頁嘅「This page no longer exists」提示（只有純邏輯 unit test）冇喺 browser 入面實際觸發過。
- **無障礙**：冇用 screen reader、冇量度色彩對比；只驗證咗鍵盤操作同 `prefers-reduced-motion`。
- **效能**：冇做任何 benchmark 或 load test，所以冇吞吐量、延遲或大記憶體（例如 256 GB）容量嘅說法；checkpoint 約 780,000–792,500 bytes（約 4,300 筆、每筆約 181–184 bytes）、耗時約 59–136 ms 只係幾次單次觀察。
- **真實來源**：Solace／MSCI／Barra／MSSQL 全部冇連，相關 adapter 能力未確認。
- **SMB／UNC 或 bind mount 嘅 checkpoint 目錄**冇試過。
- **真實 168 小時 snapshot 週期**冇等過（排程邏輯只用 `FakeTimeProvider` 測）。
- **Code coverage、dependency 漏洞掃描**冇做；repo 入面冇 CI。

---

## 15. 已知限制

- **只係 latest-state**：冇歷史、冇 tick replay；UI 嘅閃爍只反映兩次 polling 之間嘅淨變動。
- **所有來源係 mock**；真實來源嘅 snapshot 一致性、排序同刪除通知能力未確認（見 §13）。
- **Revision 係 source-local**，跨來源唔可比較；跨來源冇全域一致性保證（checkpoint 每個來源 slice 自身一致，跨來源係「模糊」切面）。
- **Mock streaming 冇刪除訊息**，下市要等下一次完整 snapshot；Polling 亦睇唔到刪除。完整 snapshot 預設每週一次（`Snapshot:IntervalHours=168`）。
- **Stale 係讀取時即時計算**；`Stale` 只反映 feed 活動時間，唔代表數值錯。
- **強制結束冇 shutdown checkpoint**：只剩最近一次定時 checkpoint（預設每 30 秒，加上擷取到寫入之間嘅空檔）。`docker kill` 亦唔會觸發 restart policy。
- **Checkpoint 原子取代取決於檔案系統**：本機磁碟通常可以，SMB／UNC 網絡共享唔保證。恢復後，定時保存會跳過直至資料有變動（UI 顯示 `NONE YET` + `restored at startup` 係正常）。
- **Admin endpoints 冇認證**，frontend proxy 亦會轉發 `/api/admin/*`；Compose 預設只綁 `127.0.0.1`。真實環境要加 authn／authz，或者設 `DataHub__Api__AdminEnabled=false`。
- **Filter 係線性掃描、排序係 O(M log M)**（matching rows，冇 index）；offset pagination 唔隔離，價格／時間排序期間更新可以令行跳頁。CSV cap 只限輸出行數，唔限制所有查詢嘅排序記憶體。
- `price` 喺 JS 係 double；price bound query 保留輸入字串，CSV 由 backend decimal 輸出。呢個 PoC 冇針對大 data set 做效能驗證。
- 只有一個 backend instance：冇 HA、冇多實例協調；checkpoint 目錄唔可以被多個 instance 共用。
- 真實部署仍需要：認證授權、監控／告警、容量測試、secret 管理、MSSQL 等持久化方案評估。

---

相關文件：[doc/technical-spec.md](doc/technical-spec.md)（技術設計）、[frontend/README.md](frontend/README.md)（frontend 指令同目錄）、[.github/copilot-instructions.md](.github/copilot-instructions.md)（專案規則）。

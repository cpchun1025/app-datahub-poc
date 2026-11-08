---
description: '建立 Trading Data Hub PoC，包括後端、前端、Docker、測試及技術文件'
agent: 'agent'
---

請依照以下要求實作 Data Hub PoC。

你係一位資深 .NET / React 工程師。請喺目前 workspace 建立一個可以實際運行嘅 Data Hub PoC，唔好只提供架構說明或 pseudocode。

先檢查現有 workspace，避免覆蓋無關檔案。簡短列出實作計劃，然後直接開始建立程式碼。如果遇到未提供嘅外部連線資料，使用明確標示嘅 mock adapter，唔好虛構真實 API 格式。

# 1. 業務背景

呢個係供 Trading 業務使用嘅 latest-state Data Hub。

數據來源包括：
- 多個外部 API，例如 MSCI、Barra 等。
- Solace 訂閱消息。
- 各來源嘅完整 snapshot。

正常運作：
1. 初次建立數據時，向各來源取得 snapshot。
2. 完整 snapshot 預設每星期刷新一次。
3. 期間持續接收 Solace 更新，亦可能由外部 API 定時更新。
4. 喺本地維護最新狀態。
5. 提供 API 畀其他 client 同 React Dashboard 使用。

只需要保存最新狀態：
- 例如收到 100 → 101 → 100，最後提供 100 就足夠。
- 唔需要 tick history、event sourcing 或完整事件重播。
- 唔做 booking 或 trade processing。
- 中間更新可以唔保留，但唔可以將過時數據默認標示成最新數據。

# 2. 現有環境及限制

現有資源：
- Windows Server 2022
- 一部有 256GB RAM 嘅伺服器
- MSSQL Server 2022
- Network Share
- 外部 API
- Solace

禁止新增依賴：
- Redis
- PostgreSQL
- Kafka
- 其他額外數據庫、message broker 或基建服務

技術選擇：
- Backend：C#、.NET 10、ASP.NET Core
- Frontend：React 19、TypeScript、Vite
- Node.js：24 LTS
- 使用相容穩定版套件，唔用 preview。
- 唔需要 Java。

# 3. 開發及啟動方式：重要

必須支援兩種獨立模式。

## A. 本地開發，不依賴 Docker

Backend：
- 可以用 Visual Studio 開啟 solution。
- 可以直接 F5 debugger 啟動。
- 同時支援 dotnet run。
- 提供 launchSettings.json。
- 使用固定、清楚記錄嘅開發 port，例如 http://localhost:5080。

Frontend：
- 可以獨立執行 npm install / npm run dev。
- 例如 http://localhost:5173。
- 唔需要由 .NET 啟動 npm。
- 唔需要先啟動 Compose。
- Backend 同 Frontend 分開啟停。
- 處理好開發環境 CORS 或 Vite proxy。
- Backend 未啟動時，UI 顯示 disconnected，唔好直接 crash。

提供 .env.example 及 backend 設定範例。

## B. Docker Compose

提供：
- Backend multi-stage Dockerfile。
- Frontend multi-stage Dockerfile，build 後用輕量 web server 提供靜態檔案。
- compose.yaml。
- .dockerignore。
- Backend checkpoint persistence volume。

docker compose up --build 應該可以啟動完整 mock PoC。

Compose 只需要 backend 同 frontend：
- 唔好建立 Redis、SQL Server、Solace broker 或其他額外服務。
- 使用 mock sources，因此唔需要連接公司網絡先跑得到。
- 正確處理 browser URL 同 container internal hostname，唔好叫 browser 存取 http://backend:5080。
- 可以用 frontend server reverse proxy 將 /api 轉發去 backend。

預設可以提供 Linux containers，但 README 必須清楚說明需要 Linux-container runtime。
唔好聲稱呢套 Linux images 可以直接喺原生 Windows Server 2022 Windows-container runtime 運行。
Windows Server 嘅非 Docker 啟動方式必須保留。

# 4. PoC 範圍

請做一條完整可驗證流程：

Mock sources
→ Initial snapshots
→ In-memory latest-state store
→ Continuous mock updates / mock API polling
→ Periodic checkpoint
→ HTTP API
→ React Dashboard

程式開始時自動使用 Mock mode，唔需要 credentials。

Mock adapters 必須明確標示係模擬，唔好聲稱已經完成 MSCI、Barra 或真實 Solace 整合。

# 5. 多來源數據模型

使用一個簡單、強型別模型，例如：
- SourceId
- InstrumentId
- Price
- Currency
- SourceUpdatedAtUtc
- ReceivedAtUtc

價格用 decimal。
API 時間用 UTC ISO 8601。

數據 key 至少包含 SourceId + InstrumentId：
- 相同 instrument 喺不同來源唔可以互相覆蓋。
- 暫時唔做跨來源 golden-price selection 或 instrument mapping。

另外維護每個來源嘅狀態，例如：
- Initializing
- Live
- Recovering
- Stale
- Disconnected

Mock mode 同來源狀態都要可以喺 UI 睇到。

呢個簡化模型只係 PoC 示範，唔代表所有 MSCI / Barra 數據都係 price records。

# 6. Backend 設計

保持簡單、清晰，唔好 overengineer：
- ASP.NET Core API
- BackgroundService 處理 mock feeds、polling 同 checkpoints
- Singleton latest-state store
- 可替換嘅 source adapters
- 可替換嘅 checkpoint persistence abstraction

用少量合理 interface 分開：
- 取得 snapshot
- 接收或產生更新
- 最新狀態儲存
- Checkpoint 讀寫

唔需要大量空 projects、generic repository framework、CQRS 或 MediatR。

In-memory store 係 API 主要讀取路徑：
- API request 唔可以逐次查 DB 或讀 checkpoint。
- 正確處理 concurrent reads / writes。
- 優先使用 immutable records 或其他安全替換方式。
- 唔好只因為用 ConcurrentDictionary 就假設複合操作自動一致。
- 避免喺鎖內執行 network I/O 或 disk I/O。

提供至少三個 mock sources：
- 兩個模擬 snapshot + streaming updates。
- 一個模擬外部 API snapshot + periodic polling。

Mock 數據量、更新頻率同 polling interval 都可以設定。

# 7. Snapshot 與更新協調

每個來源獨立管理 snapshot / refresh。
一個來源失敗，唔應該令所有來源停止服務。

要處理 snapshot refresh 同即時更新撞埋一齊嘅情況：
- 唔可以用較舊 snapshot 覆蓋 refresh 期間收到嘅較新更新。
- 為 mock source 定義清楚、可測試嘅 source-local revision / ordering 語義。
- 用簡單合理方式協調 snapshot 與更新。
- 唔需要持久化所有中間事件。
- 唔好假設不同來源嘅 timestamp 或 revision 可以互相比較。

完整 snapshot 應該有清楚 membership 語義：
- 如果某筆數據已經唔喺新完整 snapshot，刷新完成後唔應永遠殘留。
- 同時唔好刪除 refresh 期間有效新增嘅數據。

真實來源接駁時，snapshot consistency / sequence 支援要另行確認，README 寫清楚限制。

# 8. Persistence 與重啟恢復

第一版用 file-based checkpoint，唔強制要求 SQL Server 先跑得起。

原因：
- 先驗證 RAM latest-state + persistence + recovery。
- 呢個唔代表否定之後使用現有 MSSQL。

要求：
- 保存目前最新狀態，而唔係歷史事件。
- 保存必要嘅 per-source metadata、checkpoint version 同保存時間。
- Checkpoint directory 可設定。
- 本地開發預設用本地資料夾。
- Windows 可設定為有權限存取嘅 Network Share / UNC path。
- Docker 使用 mounted volume。

Checkpoint interval 同 weekly snapshot interval 係兩回事：
- 完整 source snapshot：預設七日一次。
- 本地 checkpoint：例如每 30 秒一次，可設定。

持久化：
- 唔好每次價格更新都同步寫檔。
- 喺 background 保存。
- 唔允許 checkpoint jobs 無限重疊。
- 使用 temp file + replace / rename 等方式，避免直接覆寫唯一有效檔案。
- 保留上一份有效 checkpoint 作 fallback。
- 寫檔失敗時保留 in-memory serving，同時顯示／記錄錯誤。
- 說明 atomic replace 嘅實際保證取決於 filesystem / network share，唔好作絕對保證。

重啟：
1. 有有效 checkpoint，先載入。
2. 將恢復數據標示為 Recovering / Stale，而唔係直接 Live。
3. 嘗試向各來源取得最新 snapshot，或者透過其可用能力重新同步。
4. 個別來源同步成功後，先標示為 Live。
5. 冇 checkpoint 或檔案損壞，fallback 至 source snapshot。
6. 來源不可用時，可以提供已恢復數據，但 API/UI 必須顯示 freshness 狀態。

中間 updates 丟失可以接受；但要解釋 checkpoint 之後、down 機之前嘅最新值未必已保存。

唔需要跨來源全域 transactionally consistent snapshot。

# 9. API

提供少量清晰 endpoints，例如：
- GET /api/health
- GET /api/sources
- GET /api/prices?sourceId=...&instrumentId=...&page=1&pageSize=100
- GET /api/prices/{sourceId}/{instrumentId}
- POST /api/admin/snapshot/{sourceId}
- POST /api/admin/checkpoint

要求：
- List endpoint 有 pagination，同合理 page-size 上限。
- Response 包含或可以查到 source freshness / recovery 狀態。
- 提供 OpenAPI。
- Manual snapshot 同 scheduled snapshot 唔可以對同一來源無控制地重疊。
- Admin endpoints 只供 PoC 使用，README 說明未加認證，唔可以直接暴露到 production。

# 10. React Dashboard

建立一個簡單、可用嘅 dashboard：
- Backend connection status
- Mock mode badge
- 每個來源嘅連線／恢復狀態
- 每個來源嘅 record count
- Last successful snapshot time
- Last received update time
- Last successful checkpoint time / last persistence error
- Prices table
- Source filter
- Instrument search
- Pagination
- 手動 refresh source snapshot 按鈕
- 手動 checkpoint 按鈕

第一版用可設定嘅 HTTP polling，例如每秒一次。
唔需要第一版就加入 SignalR 或 WebSocket。

正確處理：
- Loading
- Empty state
- API error
- Backend disconnected
- Stale / recovering data
- Component unmount 時取消 request 或清理 timer
- 避免慢 request 導致 polling 無限制重疊

唔好一次將所有數據載入 browser。

# 11. Tests

至少提供 backend 自動化測試：
- 100 → 101 → 100 後只讀到最後嘅 100。
- 不同來源相同 InstrumentId 唔互相覆蓋。
- Checkpoint save / load round trip。
- Checkpoint 損壞嘅 fallback 行為。
- 恢復後唔會立即錯誤標示為 Live。
- Snapshot refresh 唔會覆蓋較新更新。
- 完整 snapshot 嘅移除／新增處理。
- 一個來源失敗唔影響其他來源。

時間同 intervals 盡量可注入／控制，避免測試靠長時間 sleep。

# 12. README 及交付

提供：
- 清楚嘅 folder structure
- Visual Studio 開啟及 F5 步驟
- dotnet run / dotnet test 指令
- npm install / npm run dev / npm run build 指令
- Docker build / compose 指令
- Port 同環境變數列表
- Checkpoint 位置同 volume 說明
- Mock source 行為
- 故障恢復驗證步驟
- 已知限制
- 之後接真實 Solace / external API / MSSQL 嘅 extension points

特別提供以下手動驗證流程：
1. 分別啟動 backend 同 frontend。
2. 觀察 snapshot 數據同持續更新。
3. 手動保存 checkpoint。
4. 停止 backend，保留 frontend 運行。
5. UI 顯示 disconnected。
6. 重新啟動 backend。
7. 觀察 checkpoint recovery，同各來源重新同步。
8. 用 Docker Compose 跑同一條流程，驗證 container 重建後資料仍然存在。

完成後：
- 如環境允許，實際執行 backend build / tests 同 frontend build。
- Docker 可用先執行 Compose 驗證。
- 唔好聲稱執行過未執行嘅測試。
- 列出完成項目、執行結果、未驗證部分同已知限制。

重點係一個簡單、可運行、可以逐步擴展嘅 PoC，唔係一次過建立 production trading platform。

## Frontend look and feel：Bloomberg Terminal-inspired

我想要 Bloomberg Terminal-inspired 嘅金融終端風格，
唔係一般 SaaS dashboard。

只借鑑資訊密度、配色同操作感：
- 唔使用 Bloomberg logo、商標、專有素材。
- 唔需要像素級複製 Bloomberg 介面。

視覺方向：
- 深黑／炭灰色背景。
- 橙色／琥珀色作 section title、active tab 同重點提示。
- 高對比文字，輔以青色等顏色區分資訊。
- 緊湊排版、高資訊密度。
- 細邊框、方角或極小圓角。
- 避免大量留白、大型 rounded cards、漸變背景及裝飾性圖案。
- 使用系統 monospace font 顯示價格、代碼、時間及狀態。
- 數值靠右對齊，使用 tabular numerals。
- 價格精度按設定顯示，唔好硬編碼全部兩位小數。

版面：
- 頂部：Data Hub 名稱、MOCK 標示、backend connection status、UTC clock。
- 緊湊 toolbar：source filter、instrument search、refresh/checkpoint actions。
- 來源狀態區：各來源 freshness、record count、snapshot/update 時間。
- 主體：高密度 prices table，sticky header、清楚 row selection。
- 底部 status bar：最後 UI refresh、checkpoint 狀態及錯誤摘要。

互動：
- 價格相對上次畫面值上升／下降時，短暫以綠色／紅色提示。
- 第一次載入唔當成價格變動。
- UI polling 只反映兩次畫面讀取之間嘅淨變化，
  唔好聲稱展示咗所有市場 tick。
- 遵守 prefers-reduced-motion，避免持續閃動。
- Stale / Recovering / Disconnected 要有清晰文字及顏色提示。
- 唔可以只靠紅綠色傳達狀態。
- 支援基本鍵盤操作及可見 focus indicator。
- Loading 或更新時盡量保留現有表格，避免畫面不斷跳動。
- Desktop-first，較窄畫面容許表格橫向捲動。

請實際完成以上樣式，唔好只交付無樣式 HTML table。
優先使用 CSS variables 定義 terminal theme，方便之後統一調整。
第一版唔需要新增大型 UI framework 或付費 grid 套件。

## 設計文件交付

除 README 外，必須建立：

doc/technical-spec.md

呢份係技術設計文件，唔係 README 嘅複製版。
使用繁體中文（香港用語），技術名稱、程式識別字保留英文。

內容必須對應實際交付嘅程式碼；清楚區分：
- 已實作
- PoC 簡化／限制
- 未來可擴展，但今次未實作

至少包括：

1. 系統目的與範圍
   - Trading latest-state Data Hub。
   - 點解唔保存中間 tick。
   - Non-goals：歷史庫、booking、trade processing 等。

2. 技術選型
   - .NET、React、Node.js 及主要套件實際版本。
   - 選擇原因。
   - 現有基建限制，冇新增 Redis 等服務。

3. 系統架構
   - 以 Mermaid 畫 component / data-flow diagram。
   - 描述 source adapters、background services、
     in-memory store、checkpoint、API 同 UI 嘅關係。

4. 數據模型
   - Record key、欄位、型別、UTC 時間語義。
   - Per-source metadata。
   - Source-local revision / ordering 定義。
   - Freshness / recovery 狀態定義及轉換條件。

5. 核心流程
   - Cold start，冇 checkpoint。
   - Warm start，由 checkpoint 恢復。
   - 每星期 source snapshot refresh。
   - Streaming updates / API polling。
   - 定時及手動 checkpoint。
   - Manual snapshot refresh。
   - 用 Mermaid sequence diagrams 說明啟動恢復，
     以及 snapshot 同 updates 嘅協調。

6. Concurrency 與一致性
   - Shared-state thread safety。
   - Snapshot refresh 點樣避免覆蓋較新更新。
   - 完整 snapshot 嘅 record removal / membership 語義。
   - Checkpoint 係一致切面定 fuzzy snapshot，
     其 metadata 同保存數據有咩對應保證。
   - Per-source 保證與跨來源限制。
   - 唔好宣稱未實作嘅全域一致性。

7. Persistence 與 recovery
   - File format / schema version。
   - 寫入、替換、backup 同 corruption fallback。
   - 本地 filesystem、Network Share 同 Docker volume 嘅差異。
   - Crash 時可能遺失嘅更新範圍。
   - 點樣重新取得最新狀態。
   - Checkpoint 寫入失敗／來源不可用時嘅行為。

8. API specification
   - 實際 endpoints、request / response examples。
   - Pagination、validation、錯誤回應。
   - Freshness metadata。
   - PoC admin endpoints 嘅安全限制。

9. Frontend 設計
   - Terminal-inspired theme、版面及主要元件。
   - Polling、loading、disconnected 同 stale 行為。
   - 價格變動提示嘅實際語義。
   - Browser 唔會保存或渲染全部 Hub 數據。

10. Configuration 與 deployment
    - 主要設定、環境變數、預設值。
    - Visual Studio / dotnet run 同 npm run dev。
    - Docker Compose 架構、ports、proxy、volume。
    - Linux-container runtime 要求與 Windows Server 限制。
    - Secrets 唔可以提交入 repository。

11. 測試與驗證
    - 自動化測試範圍。
    - 手動 recovery 驗證步驟。
    - 已執行及未執行嘅驗證。
    - 未做 benchmark 就唔好聲稱支援特定吞吐量、
      延遲或可以充分利用 256GB RAM。

12. 已知限制與後續接駁
    - 真實 Solace、MSCI / Barra API 嘅 adapter 接入位置。
    - 真實來源需要確認嘅 snapshot / ordering 能力。
    - MSSQL persistence 嘅可能擴展位置，但唔當成已實作。
    - Production 前需要補充嘅認證、授權、
      監控、容量測試等。

README 必須連結至 doc/technical-spec.md。
完成實作後更新技術文件，確保 ports、設定、
class / interface 名稱同實際程式一致。

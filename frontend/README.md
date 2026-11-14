# Data Hub Frontend

React 19 + TypeScript + Vite 嘅 Data Hub dashboard（Bloomberg Terminal 風格深色、高密度介面）。
UI 顯示 backend 嘅 equity latest-state 同 bounded synthetic news metadata；唔係交易或歷史平台。

完整架構同設計請睇 [根目錄 README](../README.md) 同 [doc/technical-spec.md](../doc/technical-spec.md)。

## 指令

| 指令 | 用途 |
|---|---|
| `npm install` | 安裝依賴 |
| `npm run dev` | 開發伺服器 `http://localhost:5173`；`/api` 會 proxy 去 `DATAHUB_DEV_PROXY_TARGET`（預設 `http://localhost:5080`） |
| `npm run build` | `tsc -b` 型別檢查 + `vite build` 輸出到 `dist/` |
| `npm run preview` | 用 production build 喺 `http://localhost:4173` 預覽（同樣有 `/api` proxy） |
| `npm run lint` | oxlint（包括 react hooks 同 jsx-a11y 規則） |
| `npm test` | vitest：poller、pricePoller、狀態 reducer、flash、API client 同 price／news table SSR |

Backend 要先行（`dotnet run`，預設 `http://localhost:5080`）；UI 會顯示「BACKEND DISCONNECTED」並自動重試，唔會白畫面。

Grid 提供 server-side sorting／filters、技術欄位 visibility 同 CSV。Filter／sort／page-size 轉換會重設頁碼。Pause 只停價格自動刷新，health／sources monitoring 繼續；暫停時 query 改變仍查一次，Resume 即時刷新。CSV 係 request-time data，唔係 paused 畫面副本；Excel 匯入 INSTRUMENT 應選文字型別。格式、row cap 同使用限制見根目錄 README §12。

EQUITIES／NEWS 可切換；news 用獨立 publication time、category、headline 同 details，冇 news CSV。新聞來源連線狀態同文章時間分開顯示。

## 設定

見 [.env.example](./.env.example)。`VITE_*` 係 build 時寫入，改咗要重新 `npm run build`（Docker 用 build arg）。

## Node.js 版本

專案目標係 Node.js 24 LTS（`.nvmrc`、`engines`、Docker image 都係 24）。
本機開發機（Windows）實際用緊 Node v26 跑過 `npm install`／`test`／`build`／`lint`。Node 24.22.2 喺 `node:24-alpine`（Linux container）入面跑過 `npm ci`、`lint`、`test`、`build`；Windows 本機 Node 24 未驗證。

## 目錄

```text
src/
  api/          types（對應 backend DTO）、fetch client（timeout、錯誤分類）
  lib/          poller、pricePoller、grid、dataHubState（reducer）、flash、format、status、checkpointView
  hooks/        useDataHub（polling）、useNow、useDebouncedValue、useAdminActions
  components/   Header、Toolbar、Banners、SourcePanel、GridControls、PricesTable、PriceDetail、Pagination、StatusBar、ErrorBoundary
  styles/       theme.css（色彩 token）、app.css
```

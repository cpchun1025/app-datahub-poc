---
description: 改善現有 prices grid，新增 server-side sorting、filtering 同 CSV export
agent: agent
---

請閱讀專案指示、README、technical-spec，
並檢查現有 prices API、store query、frontend table 同 polling logic。

先簡短列出計劃，然後直接實作。
保留現有 terminal UI，唔重寫 ingestion、source adapters 或 checkpoint。

## 1. Backend query

為 /api/prices 增加：
- sortBy
- sortDirection
- currency filter
- sourceStatus filter
- minPrice / maxPrice

保留現有 sourceId、instrumentId、page、pageSize。
Market filter 等有可靠 market metadata 後先加，唔虛構欄位。

要求：
- Filter → sort → paginate，全部喺 backend。
- Sort fields 用 allowlist。
- 至少支援 instrument、source、price、currency、
  source time、received time。
- 數字按數字排序，時間按時間排序。
- 明確定義 null ordering。
- 最後以 SourceId + InstrumentId 作 stable tie-breaker。
- 無效 query 用現有 problem+json convention。
- 同一請求使用一致嘅狀態評估時間。
- Table、count 同 CSV 共用 query semantics。
- 避免將複雜 query code 全部堆入 endpoint。

Latest-state 會喺不同請求之間改變；
文件唔可以聲稱跨頁係全域一致 snapshot。

## 2. Frontend grid

保留現有色彩、資訊密度、鍵盤操作、flash、
reduced-motion、disconnected 同 recovery 行為。

新增：
- 點 column header 切換 ascending/descending。
- 清楚顯示 sort indicator 及 aria-sort。
- Currency、status、price range filters。
- Reset filters/sort。
- Column visibility，容許隱藏技術欄位。
- Filter/sort/page-size 改變時返回第一頁。
- 保留 search debounce。
- Stable row identity，唔用 array index 作 row key。

可考慮 TanStack Table；
如果现有 table 容易擴充，亦可以保留現有實作。
唔引入大型 UI framework 或付費 grid dependency。

Sorting 必須影響完整 matching dataset，
唔可以只排序 browser 收到嘅當前頁。

## 3. Pause display

新增 Pause updates / Resume：
- 暫停 prices table 自動刷新，顯示 DISPLAY PAUSED。
- Backend ingestion 繼續。
- Backend connectivity monitoring 繼續，
  唔可以將斷線隱藏成 CONNECTED。
- 暫停時 filter、sort、page 改動仍可觸發一次查詢，
  但唔恢復定時刷新。
- Resume 立即刷新，再恢復正常 polling。

## 4. CSV export

新增 backend endpoint，例如 /api/prices/export.csv。

支援：
- Export current page：按目前 query/page 重新查詢。
- Export all matching：忽略 pagination，但保留 filter/sort。

清楚標示 export 係 request-time data，
唔保證逐格等於之前已顯示或 paused 嘅畫面。

要求：
- Backend export，唔由 browser 逐頁拼接。
- 使用有界 immutable/copy view，避免 export 途中資料混雜。
- Cross-source consistency 限制按現有 store 保證寫清楚。
- 設 configurable export row limit。
- 超限明確拒絕，唔靜默截斷。
- 支援 cancellation，唔長時間持有 ingestion lock。
- UTF-8 BOM、正確 CSV escaping。
- UTC ISO 8601 timestamps、invariant numeric formatting。
- 防範文字欄位 CSV formula injection，
  並記錄採用策略及其資料表示取捨。
- 唔將正常負數價格當惡意公式。
- 唔用 ="ticker" 形式保留前導零。
- 文件提醒 Excel 匯入 ticker 時應選文字型別。
- Content-Disposition 提供安全 filename。
- Response no-store。

## 5. Tests

至少涵蓋：
- 跨頁排序正確、tie-breaker、null handling。
- Filters 組合、invalid query。
- CSV quoting/newlines/Unicode/formula injection。
- Export limit。
- Table/export query consistency。
- Pause/resume、query change、poll overlap regression。

執行 backend build/tests、frontend lint/tests/build。
可用 browser tools 時驗證 UI；否則列為未驗證。

更新 README 同 technical-spec，
列出實際變更、驗證結果及限制。
唔自動 commit 或 push。
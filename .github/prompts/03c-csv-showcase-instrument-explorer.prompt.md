---
description: 改善 source UI、建立 instrument explorer，並完成 local/UNC CSV ingestion showcase
agent: agent
---

請在現有 repo 實作以下功能。
這是現有 API-first Data Hub 的增量擴展，不是全面重寫。

先閱讀：
- .github/copilot-instructions.md
- README.md
- doc/technical-spec.md
- doc/api-first-review.md，包括 follow-up
- 現有 source registration、adapters、store、revision ordering
- Snapshot membership/removal、checkpoint/recovery
- Prices/news API、query/export semantics
- React source panel、row selection、polling
- clients/python/

先檢查 git status，保留現有修改。
列出短計劃、預期修改位置及 schema compatibility 策略，
然後分階段實作。

不要啟用外部來源。
不要開始 CNFIN/Yahoo 工作。
不要 commit 或 push。

# 1. 本次目標

完成兩條可實際驗證的流程：

Synthetic CSV generator
→ Local folder 或 Windows UNC/network share
→ .NET CSV adapters
→ Latest-state store
→ Checkpoint/recovery
→ HTTP API
→ Python client 與 React UI

使用者選擇 instrument
→ 查看該 instrument 的 spot observations
→ 查看 synthetic index memberships
→ 查看來源及 freshness

Dashboard 只是 consumer。
所有新增資料能力必須可經 API 使用。

# 2. 正確資料語義

分清：
- Source：資料提供者／ingestion origin。
- Instrument：穩定 instrument identity。
- Observation：某來源對 instrument 提供的價格資料。
- Membership：instrument 屬於某 index/basket 的關係。

同一股票屬於多個 index，不應因此複製成多筆 spot quotes。

不同來源對相同 instrument 的價格不保證相同。
不可自動平均、合併或宣稱唯一正確價格。

本次不建立：
- Golden-price engine。
- 通用 instrument master。
- 自動跨 vendor symbol matching。
- Futures/options/repo pricing model。
- Corporate-action processing。
- 全市場 index coverage。

Instrument explorer 先實作：
1. Spot observations。
2. Synthetic index memberships。

Related derivatives/financing 只在文件列為未來擴展。
UI 不要展示虛構合約、repo rates 或假裝已有資料的 tabs。

# 3. Source monitoring UI

目前 source 區會隨來源數目增長佔滿畫面。
改成 compact、可收合的 monitoring panel：

- 收合時顯示來源總數及各主要 health 狀態數目。
- 有錯誤時，收合狀態仍可見 warning/error count。
- 展開後支援 source name/id search。
- 支援實際存在的 source kind、health filters。
- 固定合理最大高度，內部捲動或分頁。
- 清楚顯示 filtered count / total count。
- 狀態不可只靠顏色。
- 支援鍵盤、focus indicator、可存取的展開按鈕。

Monitoring filters 與 prices query filters 是不同用途，
請分開標示，不要靜默互相改動。

以 synthetic frontend fixtures 驗證至少 30 個 sources。
不要為 UI 測試在 default runtime 真正新增 30 個 runners。

不新增大型 UI framework。
沿用 terminal theme。

# 4. Instrument identity

保留現有 SourceId + InstrumentId observations key，
不要破壞 existing API 或讓不同 sources 互相覆蓋。

CSV demo 以明確 schema/config 提供 stable InstrumentId，
provider/display symbols 一律保留 string。

可使用 0005.HK、0700.HK 等作展示 symbol，
但：
- 價格及 memberships 全部標示 SYNTHETIC。
- Index IDs 使用 DEMO 前綴。
- 不宣稱 synthetic membership 係真實 index composition。
- 不由 ticker 自動猜測 currency、exchange 或其他欄位。
- 不以模糊 symbol comparison 合併 instruments。

Index membership 必須引用相同 InstrumentId。

# 5. CSV demo schema

第一版使用兩個明確 schema，不做任意 CSV mapping wizard。

## A. Equity spot observations

建議欄位，按實際 model 調整並文件化：

- InstrumentId
- ProviderSymbol
- Price
- Currency
- SourceUpdatedAtUtc
- PriceKind（例如 Last；不是 real-time 保證）

SourceId 由受信任設定決定，
不要接受 CSV row 任意覆蓋其他 source。

Price 使用 decimal、InvariantCulture。
空值、invalid decimal、NaN、Infinity 不可轉成 0。

Timestamp 要有明確 offset/UTC。
Missing/unknown 語義必須符合 model，
不能用 file mtime 冒充市場 observation time。

## B. Synthetic index composition

例如：
- IndexId
- InstrumentId
- Weight
- EffectiveDate

Native CSV DTO、parser/validation、internal mapper 分開。

Weight 明確採用 fraction 或 percentage，不可混用。
EffectiveDate 是 date-only，不可虛構 UTC instant。

同一 instrument 可以屬於多個 demo indices。
Membership 唔可以插入 prices store 冒充 price record。

# 6. Synthetic generator

提供容易執行的 generator，可使用標準庫 Python，
但它只是 demo 工具，不是 backend runtime dependency。

要求：
- 可指定 output directory。
- 支援 deterministic seed。
- 產生至少 20 個 instruments。
- 至少 2 個 synthetic indices，有重疊成員。
- 產生 initial 及 next scenario。
- Next scenario 包括價格變更、新增及移除 records、
  membership/weight 變更。
- 不依靠長時間 sleep 產生下一版本。
- 提供 malformed scenario，驗證拒絕及 cache 保留。

使用 temp file + replace 發布每個完整檔案，
不逐行覆寫 backend 正在讀取的檔案。

只能在明確的 demo/output directory 操作。
不覆寫 unrelated files。
Runtime output 必須 gitignored。

可以 commit 細小、明確 synthetic tests fixtures。
不 commit 真實市場檔案或 runtime checkpoints。

# 7. CSV ingestion adapters

.NET backend 實作真正 file-based ingestion adapters。

- 使用成熟、相容穩定版 CSV parser。
- 不用 Split(',') 解析 CSV。
- 支援 quoted fields、escaped quotes、CRLF/LF、UTF-8 BOM。
- 不新增 service、database、broker 或 Python runtime requirement。
- Default mode 保留現有 mocks。
- CSV sources 預設停用，有明確 Enabled/config examples。
- CSV failure 不阻止 backend 啟動或其他 sources ingestion。

File paths 只從 operator config 取得：
- Local absolute/relative path，清楚定義解析基準。
- Windows UNC path。
- 不提供任意 user-supplied path 的 HTTP read/upload endpoint。
- 不建立 recursive file crawler。
- 不在 API/error response 暴露完整敏感 UNC path 或 credentials。

可設定 polling interval，demo 預設例如 5 秒。
定義 file size、row count、field length limits。

每個 CSV source 最多一個 import in flight。
Scheduled/manual import 若共用入口，必須協調。
支援 cancellation。
不要在 store lock 內讀檔或做 parsing。

Network share 可能慢、斷線或 block：
- 有 bounded retry/backoff。
- 不因 timeout 不斷建立仍在執行的背景 jobs。
- 文件說明 filesystem I/O cancellation 的實際限制。

Polling 為基礎；
FileSystemWatcher 如使用，只作提示，不是唯一可靠機制。

# 8. File publication / completeness

為每個 CSV source 定義：
「每份成功驗證的檔案是該 source/dataset 的完整 snapshot」。

流程：
1. 讀取 bounded candidate。
2. Parse 及完整 validation。
3. 全部成功後才 atomic apply 到該 source store。
4. 記錄 fingerprint/import metadata。

本次採用 all-or-nothing import：
- Bad row、duplicate key、missing required header、
  invalid structure：拒絕整份檔案。
- 保留上一份有效資料。
- 顯示 rows/errors/import status。
- 不把 parser failure 當成功 empty list。

Empty file/header-only 預設拒絕。
如果支援清空 dataset，必須 explicit opt-in，
不能由暫時零 bytes 自動觸發。

只有成功完整 snapshot 才可觸發 missing-member removal。

File missing、locked、share unavailable：
- 不刪除 cache。
- 不當成空 snapshot。
- 顯示 freshness/error。

避免 ingest producer 正在覆寫中的半份檔案。
明確要求推薦 producer protocol 為 temp + replace。

不能宣稱僅靠 file size/mtime 穩定就可證明內容完整；
對不遵守 publication protocol 的第三方 writers，
文件清楚列出限制。

兩份 CSV 獨立發布／匯入，
不宣稱 spot 與 composition 有跨檔案 atomic transaction。

# 9. Ordering / restart / freshness

不要以 file mtime、CSV timestamp ticks 冒充權威 source sequence。

使用現有協調機制或小型、明確的 local import ordering：
- Local revision 必須標示為 local。
- Checkpoint restore 後，新有效檔案仍可被接受。
- 同 timestamp 但價格修訂不可被忽略。
- 相同 file content 不應反覆產生假更新。
- Clock skew 不應令 adapter 永久拒絕新檔案。
- Fingerprint 與 dataset metadata 要在成功 apply 後一致更新。

明確記錄：
本次是否以「最後接受的完整發布檔案」為準。
如没有 producer version contract，
不能聲稱可辨認操作員重放的一份舊檔案。

區分：
- Last file check。
- Last successful validation。
- Last data change/import。
- Observation time。
- Import error / source availability。

檔案未變不等於 disconnected，
但檔案存在亦不代表市場資料 fresh。

新的 composition store/checkpoint 若有需要，
採最小、typed extension，不建立 generic data platform。

保留旧 checkpoint 相容性及 corruption fallback。
新 optional sources 的 restored data
必須先 Recovering/Stale，成功重新驗證後才解除。

# 10. Instrument explorer API / UI

先實作 API，再接 UI。

提供 additive instrument detail/related-data endpoints，
具體路徑按現有 conventions 決定並記錄。

至少可查：
- Instrument identity。
- 各 source 的 spot observations。
- 每個 observation 的來源、時間、currency、
  price kind（如已知）及 freshness。
- Index memberships、weights、effective dates、source status。

保留 existing prices endpoint 作 source-level observations view。
不要無規則刪除不同 sources 的 rows。

本次 main prices table 可以維持 source-level，
但清楚標示這是 source observations，
不是 consolidated/golden prices。

點選 instrument：
- 開啟右側 detail panel；窄畫面可置於下方。
- 以 InstrumentId 查詢，而非只展示 selected row。
- 可從 panel 看其他來源 observations 及 memberships。
- Detail 查詢不能只靠 browser 當前 prices page 拼湊。
- 顯示缺少資料、loading、API error、stale 狀態。
- 清楚標示各 dataset/source 的 as-of，
  不宣稱跨 source detail 是全域一致快照。
- 大型 related lists 必須 bounded/paginated。

原有 footer raw-record details 移至可展開 Diagnostics。
Footer 保留 connection/checkpoint/error summary。

清楚區分：
- 相對上次 UI poll 的 price delta。
- 相對 previous close 的 daily change。

沒有可靠 previous close 就不顯示 daily change，
也不可將 poll delta 標成 daily change。

避免 polling overlap、取消過時 selection requests、
unmount 清理資源。
Pause display 時 connectivity 仍需準確。

# 11. Attribution / mode labels

分開「傳輸方式」與「資料是否 synthetic」。

例如：
- Adapter kind：FILE / CSV。
- Data classification：SYNTHETIC DEMO。

CSV adapter 是真實 file ingestion，
但 demo payload 不是實際市場資料。

Header 不可因為有 file adapter 就聲稱
已連接 external market feeds。

啟用自有 CSV 時亦不可仍然籠統聲稱全部為 MOCK。
按實際設定顯示清楚的 source/data classification。

CSV source 不提供 mock outage control。

# 12. Python consumer showcase

擴展 clients/python/ 的最小必要能力：
- 查詢 CSV spot source。
- 查詢選定 instrument。
- 查詢其 synthetic index memberships。
- 顯示 next scenario 後的更新結果。

保留 decimal-aware parsing、time/null 語義及 bounded pagination。

Python consumer 只呼叫 HTTP API。
它不能直接讀入 CSV 來冒充 Data Hub 查詢結果。

Generator 可以寫檔；
consumer 必須透過 Hub 讀取，
文件清楚區分兩種角色。

# 13. Tests

Backend 至少涵蓋：
- CSV quoting/BOM/leading-zero string symbols。
- Decimal、timezone/date-only validation。
- Missing headers、bad rows、duplicates、limits。
- Failed/partial candidate 不覆蓋或清空 cache。
- Successful full snapshot 的 add/update/remove。
- 相同 timestamp 的值修訂。
- Unchanged content 不產生假更新。
- File unavailable 後保留 last-known state。
- In-flight import 不重疊。
- Checkpoint round trip、舊 schema compatibility、
  corruption fallback、restart ordering。
- Composition memberships 不複製 spot quotes。
- 一個 CSV source 失敗不影響 mocks。
- 新 API/OpenAPI contract 及 pagination。

Frontend 至少涵蓋：
- 30 sources 下的 compact/filter/collapse 行為。
- Source monitoring filters 不誤改 prices query。
- Instrument detail loading/error/empty。
- Selection race/cancellation。
- Synthetic labels、freshness、diagnostics。
- Membership 不被當成另一份價格。

Python：
- Synthetic mocked HTTP tests。
- 新 detail/membership contract。
- 可 opt-in localhost showcase smoke。

時間可注入，避免依靠長 sleep 的 tests。

# 14. Manual showcase documentation

新增 doc/csv-showcase.md，
並由 README 連結。

提供完整可複製的 PowerShell 指令：

1. Generate initial synthetic CSVs。
2. 設定 CSV source paths / Enabled。
3. 啟動 backend，不啟動 frontend。
4. Python 經 API 查 spot 及 memberships。
5. 啟動 frontend，filter CSV source，選 instrument。
6. Publish next scenario。
7. 驗證價格更新、record removal、
   membership changes，不重複建立 spot quotes。
8. Publish malformed scenario。
9. 驗證 last-known data 保留且 API/UI 顯示 error。
10. Publish valid file 恢復。
11. 保存 checkpoint、停止並重啟 backend。
12. 驗證 recovery 及重新讀取 CSV。

另提供 Windows UNC 設定例子：
- Backend process identity 需要 share 與 filesystem read 權限。
- 不將 credentials 寫入 repo。
- 不依賴 service account 看不到的 mapped drive letters。
- UNC/network share 若沒有實際測試，明確列未驗證。

Default Compose 保持 mock-only。
如提供 CSV Compose opt-in：
- 使用明確 read-only bind mount。
- Container path 與 host path 清楚區分。
- 不宣稱 Linux container 可直接使用 Windows UNC path。

更新 technical-spec：
- 實際 adapters/classes/config/API。
- 新 composition model。
- Import atomicity、ordering、recovery、freshness。
- 已實作／PoC 限制／未來擴展。

# 15. Verification and completion

執行相關 backend、frontend、Python tests/build/lint。
環境允許時實際跑：
generator → filesystem → backend listener → Python/UI/API。

不以 parser unit tests 或 MockTransport
冒充 filesystem-to-HTTP end-to-end 已通過。

最後列出：
1. 完成項目及檔案。
2. 實際執行命令、版本及結果。
3. 可直接複製的 showcase 指令。
4. 本機 filesystem、UNC、Docker 各自驗證狀態。
5. 未解決事項及已知限制。

不沿用上次 tests 數字當成本次結果。
完成後停止，等待確認。
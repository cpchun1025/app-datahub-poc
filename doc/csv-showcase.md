# Synthetic CSV Showcase

呢個係本機、operator-configured 嘅 synthetic data showcase，唔係 external source adapter、
市場數據供應或 production ingestion service。預設 `CsvShowcase:Enabled=false`；
未 opt in 時 backend 唔會讀取 CSV。資料流係：

```text
synthetic CSV files → .NET reader/adapters → latest-state stores → HTTP API
                                                    ├→ Python reference client
                                                    └→ React instrument explorer
```

Prices 仍以 `(SourceId, InstrumentId)` 作為 identity；同一 `InstrumentId` 不會跨來源合併。
Index memberships 使用獨立 typed store，唔會偽裝成價格紀錄。API/UI polling 只查 in-memory
stores，唔會觸發 CSV file access 或其他 upstream request。

## 可複製嘅 Windows PowerShell 流程

以下步驟使用獨立 temporary directory、loopback port 51873 同本機 synthetic files；
唔會改 repo appsettings、既有 checkpoint 或任何 external source。先喺 repo root 開第一個
PowerShell terminal，產生一個全新資料夾：

```powershell
$showcase = Join-Path $env:TEMP ('datahub-csv-showcase-' + [guid]::NewGuid().ToString('N'))
$csvDir = Join-Path $showcase 'input'
python .\clients\python\examples\generate_csv_showcase.py --output-dir $csvDir --scenario initial
$csvDir
```

複製 `$csvDir` 輸出嘅完整路徑。喺第二個 terminal 設定 backend；將下方 `$csvDir` 值換成剛才
複製嘅路徑，並從 repo root 執行：

```powershell
$csvDir = 'C:\Users\<your-user>\AppData\Local\Temp\datahub-csv-showcase-<unique-id>\input'
$showcase = Split-Path $csvDir -Parent
$env:DataHub__CsvShowcase__Enabled = 'true'
$env:DataHub__CsvShowcase__SpotPath = Join-Path $csvDir 'spot.csv'
$env:DataHub__CsvShowcase__CompositionPath = Join-Path $csvDir 'composition.csv'
$env:DataHub__Checkpoint__Directory = Join-Path $showcase 'checkpoints'
dotnet run --project .\backend\src\DataHub.Api --no-launch-profile --urls http://127.0.0.1:51873
```

等待 backend 啟動及 CSV source status 完成初次同步。第三個 terminal 可查 API：

```powershell
curl.exe http://127.0.0.1:51873/api/sources
curl.exe "http://127.0.0.1:51873/api/prices?sourceId=demo-csv&pageSize=10"
curl.exe http://127.0.0.1:51873/api/instruments/hk-0005
curl.exe http://127.0.0.1:51873/api/instruments/hk-0005/memberships
```

Python client（已按 [clients/python/README.md](../clients/python/README.md) 建立 `.venv`）可查
同一組 latest-state API：

```powershell
Set-Location clients\python
.\.venv\Scripts\python.exe examples\demo.py --base-url http://127.0.0.1:51873 --source-id demo-csv --instrument-id hk-0005
```

前端另外啟動：

```powershell
Set-Location frontend
$env:DATAHUB_DEV_PROXY_TARGET = 'http://127.0.0.1:51873'
npm run dev
```

開 <http://localhost:5173>，於 source panel 篩選 `CSV` source，再選取一個 instrument；
Explorer 透過 instrument observations/memberships API 讀取細節。此 filter 只影響 monitoring
panel，唔改價格 grid query。

### 修改、失敗保留及復原

喺第一個（generator）terminal，沿用 `$csvDir` 依次發佈下一份完整 snapshot：

```powershell
python .\clients\python\examples\generate_csv_showcase.py --output-dir $csvDir --scenario next
```

Spot rows 會加入 `hk-0025`、移除 `hk-0024`，並更新 `hk-0005` 價格；composition snapshot 亦有
weight／membership 更新。等 backend 下一次檢查後，再查上述 API。

以下情境只改 spot file，composition file 保持不變：

```powershell
python .\clients\python\examples\generate_csv_showcase.py --output-dir $csvDir --scenario malformed
```

無效 decimal 會令整份 spot snapshot 驗證失敗；API 保留最後成功套用嘅 rows，source status
提供錯誤資訊。修復方式係重新發佈完整有效檔案：

```powershell
python .\clients\python\examples\generate_csv_showcase.py --output-dir $csvDir --scenario initial
```

等驗證／同步成功，再手動保存 checkpoint：

```powershell
curl.exe -X POST http://127.0.0.1:51873/api/admin/checkpoint
```

於 backend terminal 按 Ctrl+C，然後用同一環境變數及啟動指令重新啟動。Checkpoint 會恢復
價格及 memberships；恢復資料未完成新 process 嘅 CSV 驗證／snapshot 前唔會標示為 `Live`。
完成後 Ctrl+C 停止 backend，再按需要刪除自己建立嘅 temporary showcase directory。

> 不要對未確認用途嘅既有資料夾執行遞迴刪除；上面產生嘅 `$showcase` 是新目錄，清理前仍應確認路徑。

## CSV contract

檔案由 operator 透過 `DataHub:CsvShowcase` 設定指定；無 upload endpoint。每份成功驗證檔案
代表該 product/source 嘅完整 snapshot：

| File | Required headers（名稱及次序固定） | 限制 |
|---|---|---|
| Spot | `InstrumentId,ProviderSymbol,Price,Currency,SourceUpdatedAtUtc,PriceKind` | Header 名稱及次序必須完全相同；ID 唯一、最長 256 字元且不可含 path separator／control char；Currency 為 3 個 ASCII 字母；Price 為 invariant decimal，不接受 exponent／千位分隔；時間必須有 UTC offset；只接受 `Last` |
| Composition | `IndexId,InstrumentId,Weight,EffectiveDate` | Header 名稱及次序必須完全相同；index ID 必須以 `DEMO` 開始；`IndexId + InstrumentId` 唯一；Weight 為 0–1 fraction；日期用 `yyyy-MM-dd` |

Reader 支援 UTF-8（含 BOM）、quoted fields，並驗證欄數、欄位長度、
InstrumentId 格式及整份檔案。預設上限係每檔 2,000,000 bytes、10,000 rows、每欄 256
字元；各上限可由 `DataHub:CsvShowcase:MaxFileBytes`、`MaxRows` 同 `MaxFieldLength` 調整，
但 validator 硬上限分別係 100,000,000 bytes、1,000,000 rows 同 16,384 chars。Composition
及 spot 預設都拒絕 header-only snapshot；`AllowEmptySnapshot=true` 先可以用空 dataset 清除現有狀態。

有效但內容未變嘅 spot 檔會刷新 validation/freshness，不重寫價格 store。Changed content 會
建立 adapter-local revision，再以 snapshot replace source slice；revision 不依賴 file mtime 或
CSV publication timestamp，亦不會跨 process 持續。Composition 狀態獨立追蹤 file check、
successful validation、data import、row count 同 error。失敗會保留舊資料，按 bounded
backoff 重試；Spot 由既有 source runner retry/backoff 管理。

## 發佈與恢復限制

- 建議 producer 先喺同一目錄寫 temporary file，再以 atomic replace 發佈；generator 示例採用
  `os.replace`。Spot 同 composition 是兩個獨立檔案，兩者之間冇 transaction。
- 若其他 producer 直接覆寫檔案，reader 只能確保讀到嘅 bytes 全部可解析，無法證明係完整
  publication；一份可解析但不完整嘅檔案仍會被視為新 snapshot。
- `SourceUpdatedAtUtc` 是 CSV 提供嘅 observation time，唔等於 Hub file check time 或市場即時性。
  Source connectivity／import health、publication time 同 record freshness 係不同概念。
- Checkpoint schema 3 保存 memberships、composition import status 及 spot metadata；schema 1／2
  仍可讀，但不包含 composition。Checksum/count 驗證保護 checkpoint 內容完整性，不代表外部 CSV 授權。
- 本次手動 E2E 使用 Windows 本機 filesystem；SMB／UNC 權限、原子 replace、Docker CSV mount、
  Visual Studio F5 未驗證。Linux Docker Compose 唔會預設掛載或啟用 showcase CSV。
- Synthetic data 不代表真實市場資料；程式碼 license 亦不構成第三方市場／新聞數據再分發授權。

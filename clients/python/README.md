# Python reference client

這是獨立、同步的 Data Hub HTTP consumer，不是完整 SDK、provider 或常駐 service。
它只經 HTTP 讀 prices、news、source status、instrument observations 同 memberships，
不讀 store／checkpoint、不依賴 frontend，
亦不直接連接任何外部來源。Optional Yahoo provider 是另一個未實作的 ingestion 方向，
不是這個 client。

Optional synthetic CSV showcase（包括 `demo-csv` source、完整資料產生／恢復流程及限制）
見 [CSV Showcase](../../doc/csv-showcase.md)。此例只適用於明確 opt-in 並配置 CSV files 嘅 backend。

## 環境及安裝

需要 CPython 3.11–3.15、pip，以及用作實際查詢的 .NET 10 mock backend。
本次只在 Windows／Python 3.15.2 驗證；其他 Python 版本及 POSIX 未驗證。
從 repository root 起：

Windows PowerShell：

```powershell
cd clients\python
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -c requirements-lock.txt -e '.[test]'
.\.venv\Scripts\python.exe -m pytest -q
```

POSIX shell：

```sh
cd clients/python
python3 -m venv .venv
.venv/bin/python -m pip install -c requirements-lock.txt -e '.[test]'
.venv/bin/python -m pytest -q
```

`pyproject.toml` 固定 httpx／pytest 的直接版本及 build backend；
`requirements-lock.txt` 為所需 runtime／test transitive dependencies 提供版本 constraints，
包含平台／Python 版本條件。這不是 hash-locked、跨平台 bit-for-bit build；
安裝需要取得相應 packages。測試用 `httpx.MockTransport`，預設完全離線，
不證明與實際 backend 相容。

## 只啟動 mock backend

在**另一個 terminal**，由 repository root 啟動 backend，不需要 frontend。
以下示例停用 checkpoint，避免讀寫既有 runtime checkpoints；
先確保所選 loopback port 未被其他服務佔用。

Windows PowerShell：

```powershell
cd backend
$env:DataHub__Checkpoint__Enabled = 'false'
dotnet run --project src\DataHub.Api --no-launch-profile --urls http://127.0.0.1:51873
```

POSIX shell：

```sh
cd backend
export DataHub__Checkpoint__Enabled=false
dotnet run --project src/DataHub.Api --no-launch-profile --urls http://127.0.0.1:51873
```

確認 backend 顯示已綁定該 URL 才繼續；如果啟動失敗，**不要**對該 port 上未知服務執行 smoke。
預設配置為 mock-only，毋須 credentials；完成後以 Ctrl+C 停止自行啟動的 backend。

## 執行

在 `clients/python`，使用上面 venv 的 Python。以下 Windows 指令可直接複製；
POSIX 換成 `.venv/bin/python` 及 `examples/demo.py`／`examples/smoke.py`。

```powershell
.\.venv\Scripts\python.exe examples\demo.py --base-url http://127.0.0.1:51873 --page-size 10 --max-pages 2 --sort-by price --sort-direction desc --min-price 0 --keyword synthetic
.\.venv\Scripts\python.exe examples\demo.py --base-url http://127.0.0.1:51873 --source-id mock-stream-a --instrument-id MOCK00001
.\.venv\Scripts\python.exe examples\smoke.py --base-url http://127.0.0.1:51873
```

Demo 顯示來源狀態、價格與新聞各一頁，並以 `max-pages` 示範有上限的多頁價格讀取；
提供 instrument ID 時亦會列出跨來源、但不合併嘅 observations 同 composition memberships；
同時提供 source ID 同 instrument ID 時查詢單筆價格。`--help` 列出其他 options。
`--base-url` 必須明確提供；timeout 預設 5 秒，可用 `--timeout` 設定正數秒。
錯誤顯示在 stderr，並以非零狀態結束；不自動 retry。

Smoke 是 **opt-in 真實 localhost HTTP**，只接受明確 port 的 `http://localhost`、
`http://127.0.0.1` 或 `http://[::1]`，拒絕其他 host／path；先檢查 `/api/health`
的 `mockMode`，再確認 price sources 為 mock、news source 為 `mock-news`，
只讀取一小頁資料，不呼叫 admin endpoints 或保存 responses。
它可在 mock source 尚未完成初始化時得到空列表；它不是負載或 production 測試。
若環境禁止綁定 loopback socket，請在允許的本機環境按上述步驟執行，勿當作已通過。

## 程式使用及語義

```python
from decimal import Decimal
from datahub_client import DataHubClient

with DataHubClient("http://127.0.0.1:51873", timeout=5) as hub:
    sources = hub.sources()
    prices = hub.prices(page=1, page_size=10, currency="USD",
                        min_price=Decimal("10.00"), sort_by="price", sort_direction="desc")
    one = hub.price("mock-stream-a", "MOCK00001")
    observations = hub.instrument("MOCK00001", page_size=10)
    memberships = hub.memberships("MOCK00001", page_size=10)
    news = hub.news(page=1, page_size=10, category="Markets",
                    published_from="2026-01-01", sort_direction="desc")
    batch = hub.price_pages(max_pages=2, page_size=10)
    if batch.may_have_more:
        print("上限已到，可能未讀完")
```

- 回傳 dict 保留 API 欄位及 null；只驗證 client 所需的必要結構，額外欄位保留。
  `price` 由原始 JSON 小數 token 直接解析成 `Decimal`；整數 token 只在 price 欄位轉為
  `Decimal`，分頁／revision 等仍是 `int`。不經 binary float。Price bounds 接受
  `Decimal` 或文字，拒絕 float；`Decimal` 會轉成 backend 接受的無 exponent 文字。
  Demo 只輸出文字，沒有 JSON output。
- 時間以 API 原始字串或 `None` 保留；`publishedDate`、`publishedAtUtc`、
  `originalPublicationTime`、`publicationTimePrecision` 和 `mappingWarnings` 不互相推導。
  Date-only／unknown timezone 不會偽造 UTC instant，fetch time 亦不是 publication time。
- Status、recovery 與 warnings 直接來自 API；`Live` 只係來源狀態，並不保證每筆是即時市場價格。
  Sources、prices、news 及不同 page 是獨立請求，並非原子 snapshot。
- `price_pages` 必須提供正整數 `max_pages`；每次 request 都有 `page_size`。
  到達上限時 `may_have_more=True` 提醒可能未讀完。即使結果顯示已到最後一頁，
  latest-state 亦可在請求之間改變，因而重複或遺漏，不能當成一致 snapshot。
- `TimeoutError`／`ConnectionError`、`ApiError`（含 status、ProblemDetails 的 title／detail／errors）
  與 `ContractError` 分開；非 JSON error body 也會顯示，空列表是正常結果。
  不會無限制 retry，也不自行重做 backend validation、mapping、retention 或 freshness 判斷。

## 本次驗證

2026-11-19，Windows／Python 3.15.2（Prompt 03c follow-up）：`.venv\Scripts\python.exe -m pytest -q`；
**40 passed**。測試使用本機 synthetic／MockTransport，唔打 external websites。
使用 opt-in CSV showcase 經 filesystem→backend HTTP 查 spot observations、memberships，
及 checkpoint restart 嘅手動 E2E 步驟見 [CSV Showcase](../../doc/csv-showcase.md)；
UNC／SMB、Docker mount 同 external sources 未驗證。

2026-11-16，Windows／Python 3.15.2：

- 在新的 venv 用上面的 constraints 安裝 `.[test]`，`python -m pip check` 成功。
- `python -m pytest -q`：36 passed；全部使用 synthetic MockTransport，沒有連外。
- 以 checkpoint 停用、OS 分配的 loopback port 啟動獨立 mock backend；
  `examples/smoke.py --base-url <該 URL>` 同 `examples/demo.py` 查 source、prices、
  單筆價格及 news 均成功，之後停止測試 backend。這是 localhost smoke，不代表其他部署已驗證。
- Python 3.11–3.14、POSIX、Docker、真實外部來源及負載／長時間運行未驗證。

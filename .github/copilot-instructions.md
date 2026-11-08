# Project instructions

- 呢個係 latest-state Data Hub，唔係歷史數據庫或交易系統。
- Backend 使用 C# / .NET 10 / ASP.NET Core。
- Frontend 使用 React 19 / TypeScript / Vite，Node.js 24 LTS。
- 唔好新增 Redis、PostgreSQL、Kafka 或其他基建依賴。
- Backend 必須可以獨立由 Visual Studio F5 或 dotnet run 啟動。
- Frontend 必須可以獨立由 npm run dev 啟動。
- 同時維護 Dockerfiles 同 Docker Compose。
- API 主要從 in-memory latest-state store 讀取。
- 不同來源相同 InstrumentId 唔可以互相覆蓋。
- 恢復嘅舊數據唔可以未同步就標示為 Live。
- Frontend 採用 Bloomberg Terminal-inspired 深色、高密度風格，
  唔使用 Bloomberg 商標或專有素材。
- 文件使用繁體中文（香港用語），技術名稱保留英文。
- 架構、API、設定或恢復流程有改動時，
  同步更新 README.md 同 doc/technical-spec.md。
- 唔好提交 credentials，亦唔好聲稱執行過未執行嘅測試。

## V2 scope and boundaries

本 repo 係公開 GitHub reference PoC，唔係 production trading system。

### Existing implementation
- 修改前先閱讀現有 README、technical-spec 同相關程式碼。
- 保留現有 terminal-inspired UI、mock sources、獨立啟動、
  Docker Compose、checkpoint recovery 同已存在測試。
- 文件係現況參考，唔代表每項聲稱都已由本次 Agent 驗證。
- 如果文件同 code 不一致，指出差異，按實際實作修正文檔。
- 唔好為加入功能而全面重寫已運作部分。

### External sources
- 預設仍然 mock-only，唔需要外部 credentials。
- CNFIN news 同 Yahoo Finance 係明確 opt-in 嘅 optional sources。
- 外部來源失敗唔可以影響 mock sources 或 API 基本可用性。
- 禁止連接公司系統、production database 或內部 network shares。
- 唔好將真實抓取數據、runtime checkpoints、CSV、
  secrets 或個人路徑提交到 Git。
- 唔好繞過網站登入、CAPTCHA、存取限制或限流。
- 程式碼 license 唔等於第三方市場／新聞數據再分發授權。

### Architecture
- 區分 source-native DTO、mapping 同 internal typed record。
- 相同產品可以 normalize 到同一模型。
- 不同產品唔可以硬塞入一個巨大 nullable PriceRecord。
- V2 先做 equity prices + news；FX、options、risk 暫時 out of scope。
- Yahoo 可以新增 optional Python API service。
- CNFIN 優先直接用 .NET adapter，唔需要另一個 microservice。
- 唔新增 Redis、Kafka、database 或通用 plugin platform。
- UI polling 唔可以直接觸發 external upstream requests。

### Engineering
- Sorting/filtering 喺 backend 執行，然後 pagination。
- Table 同 CSV 共用查詢語義。
- 外部輸入要驗證，HTML 唔可以直接注入 UI。
- Schema 改動要處理 checkpoint version compatibility。
- 所有新增功能都要有合理嘅 automated tests。
- Automated tests 預設唔打真實外部網站。
- 只記錄真正執行過嘅驗證；未執行要明確列出。
- 唔自動 commit、push、publish 或部署到公開服務。
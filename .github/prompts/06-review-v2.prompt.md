---
description: 審查 V2 correctness、recovery、external sources 同 public-repo readiness
agent: agent
---

請審查目前 repo，暫時唔修改檔案。

README/technical-spec 只係待核實聲稱，
唔可以代替閱讀程式碼或實際測試結果。

## Review scope

### Existing functionality
- 三個 mock price sources 有冇 regression。
- Snapshot/update races。
- Checkpoint corruption、backup fallback、shutdown/restart。
- Mock-only standalone 同 default Compose 是否仍可用。

### Grid/export
- Filter/sort 是否先於 pagination。
- Stable tie-breaker、null ordering。
- Table/export query semantics。
- CSV escaping、formula injection、row limit。
- Export consistency 同 cancellation。
- Pause display 時 connectivity 是否仍準確。
- Polling overlap、request cancellation、resource leaks。

### News
- Native DTO 同 internal model 是否真正分離。
- Retention 是否有界。
- Missing list entries 有冇錯誤刪除 cached articles。
- Publication precision/timezone 是否被虛構。
- Fetch success 同 article age 是否混淆。
- 外部 HTML/URLs 是否安全處理。
- CNFIN selectors/endpoints 是否有 discovery 依據。
- 存取限制有冇被繞過。
- 無資料同 parser failure 是否清楚區分。

### Yahoo
- Partial response 有冇造成 deletion。
- Provider/backend restart 後 ordering 是否正確。
- 同一 bar 修訂是否被忽略。
- Cache 200 有冇誤導成 upstream healthy。
- 市場休市有冇誤報 disconnected。
- BarClose 有冇被標成 real-time tick。
- Currency/timezone/leading-zero ticker handling。
- Rate limit、concurrency、retry。
- Optional profile 啟動步驟是否完整。

### Persistence/API
- 舊 checkpoint 相容性。
- Unknown schema handling。
- 新 product model 是否破壞 existing API。
- Source status 同 per-record quality 是否混淆。
- Admin endpoints 是否仍限制於本機用途。

### Public repository
- Tracked files 有冇 secrets、個人路徑、
  真實市場／新聞資料、runtime checkpoints、exports。
- Git history 檢查如未做，明確列未驗證。
- Dependency/license/data-rights 聲明是否準確。
- MCP 配置是否 optional、冇 secrets。
- README 命令、ports、source counts、測試數量有冇過時。
- 文件有冇描述其實未實作嘅能力。

## Output

按 Critical / High / Medium / Low 列出 findings：
- 檔案及行號
- 問題
- 實際影響
- 重現／驗證方法
- 最小修正建議

区分 confirmed bug、potential risk 同 unverified claim。
唔好為咗有 findings 而虛構問題。

可以執行非破壞性本機 tests/build，
但唔啟用 external sources、
唔刪 volume/checkpoints、
唔修改 code 或自動 fix dependencies。

最後列：
1. 可否繼續作個人 reference PoC。
2. 公開 repo 前必須修正項目。
3. 可後加項目，例如 GitHub Actions CI。
4. 今次實際執行過嘅驗證。

未經確認唔修改、commit 或 push。
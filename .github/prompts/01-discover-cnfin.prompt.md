---
description: 調查 CNFIN 新聞 metadata 接入方式，暫時唔實作正式 adapter
agent: agent
---

請先閱讀：
- .github/copilot-instructions.md
- README.md
- doc/technical-spec.md
- 現有 source adapters、source status、checkpoint 同 API 結構

## 目標

調查以下公開網站是否適合作為 optional news source：
- https://www.cnfin.com/
- https://www.cnfin.com/news/index.html

今次只做 technical discovery 同文件，唔建立正式 ingestion service，
唔改現有價格 ingestion 或 UI。

## 調查範圍

1. 檢查網站提供嘅使用條款、robots.txt 同公開資料使用說明。
2. 查有冇官方公開 RSS、API 或 developer documentation。
3. 如果允許存取，以少量請求檢查新聞列表：
   - 靜態 HTML 定 JavaScript-rendered？
   - 有咩 metadata？
   - 是否有穩定 article ID 或 canonical URL？
   - 時間有冇時區、年份及精度？
   - category、publisher 同原文來源如何區分？
   - 分頁如何運作？
4. Browser network 入面見到 endpoint，
   唔可以自動當成正式公開 API 或獲授權使用。
5. 確認 HTTP status、redirect、encoding 同內容結構。
6. 遇到 CAPTCHA、登入、403 或其他存取限制就停止，
   唔好嘗試繞過。
7. 如果冇 web/network 工具，明確記錄未能驗證，
   唔可以虛構 selector、API endpoint 或頁面內容。

## Metadata scope

只考慮：
- ArticleId
- Title
- Url
- Publisher
- OriginalPublisher（如有）
- Category（如有）
- Publication date/time + precision + timezone information
- FetchedAtUtc
- Language

唔抓全文、圖片、附件，唔做 sentiment 或 AI 摘要。

## 交付

建立 doc/cnfin-discovery.md：
- 調查日期
- 實際查閱 URLs
- 已確認事實／未知事項／風險
- 建議接入方式
- Metadata mapping 表
- Parser 可行性
- 建議 polling interval
- 使用條款與再分發注意事項
- 結論：可開始技術實作／需要使用者確認／暫時 blocked

robots.txt 允許唔等於取得內容再分發授權；
技術可存取亦唔等於法律上已獲允許。

唔將抓返嚟嘅真實頁面或新聞內容提交入 repo。
需要 parser fixture 時，之後使用自製 synthetic HTML。

完成後提供簡短結論，唔自動繼續下一階段。
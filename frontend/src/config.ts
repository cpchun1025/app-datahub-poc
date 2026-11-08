export const DEFAULT_POLL_INTERVAL_MS = 1000
export const MIN_POLL_INTERVAL_MS = 250
export const MAX_POLL_INTERVAL_MS = 60_000

/** 解析 VITE_POLL_INTERVAL_MS；無效值回退預設，過細／過大會被限制，避免誤設成 0 而狂 call backend。 */
export function parsePollInterval(raw: string | undefined): number {
  if (raw === undefined || raw.trim() === '') {
    return DEFAULT_POLL_INTERVAL_MS
  }

  const value = Number(raw)
  if (!Number.isFinite(value)) {
    return DEFAULT_POLL_INTERVAL_MS
  }

  return Math.min(MAX_POLL_INTERVAL_MS, Math.max(MIN_POLL_INTERVAL_MS, Math.trunc(value)))
}

export function normalizeBaseUrl(raw: string | undefined): string {
  return (raw ?? '').trim().replace(/\/+$/, '')
}

export const config = {
  pollIntervalMs: parsePollInterval(import.meta.env.VITE_POLL_INTERVAL_MS),
  apiBaseUrl: normalizeBaseUrl(import.meta.env.VITE_API_BASE_URL),
  /** 價格變動高亮持續時間（毫秒）。 */
  flashHoldMs: 900,
  /** 輸入 instrument 搜尋字之後等幾耐先發 request。 */
  searchDebounceMs: 300,
  /** 每頁行數選項（backend 預設上限 500）。 */
  pageSizes: [25, 50, 100, 200] as const,
  defaultPageSize: 100,
} as const

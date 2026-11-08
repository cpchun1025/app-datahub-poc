const NO_VALUE = '—'

const priceFormats = new Map<number, Intl.NumberFormat>()
const integerFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })

/** 價格精度由來源設定（SourceDto.priceDecimals）決定；未知精度時顯示原值，唔好硬湊兩位小數。 */
export function formatPrice(price: number, decimals: number | null | undefined): string {
  if (!Number.isFinite(price)) {
    return NO_VALUE
  }

  if (decimals === null || decimals === undefined || !Number.isInteger(decimals) || decimals < 0 || decimals > 12) {
    return String(price)
  }

  let format = priceFormats.get(decimals)
  if (!format) {
    format = new Intl.NumberFormat('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
    priceFormats.set(decimals, format)
  }

  return format.format(price)
}

export function formatInteger(value: number | null | undefined): string {
  return value === null || value === undefined || !Number.isFinite(value) ? NO_VALUE : integerFormat.format(value)
}

export function parseUtcMs(iso: string | null | undefined): number | null {
  if (!iso) {
    return null
  }

  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? null : ms
}

/** HH:mm:ss（UTC） */
export function formatUtcTime(iso: string | null | undefined): string {
  const ms = parseUtcMs(iso)
  return ms === null ? NO_VALUE : new Date(ms).toISOString().slice(11, 19)
}

/** HH:mm:ss.SSS（UTC） */
export function formatUtcTimeMs(iso: string | null | undefined): string {
  const ms = parseUtcMs(iso)
  return ms === null ? NO_VALUE : new Date(ms).toISOString().slice(11, 23)
}

/** YYYY-MM-DD HH:mm:ss（UTC） */
export function formatUtcDateTime(iso: string | null | undefined): string {
  const ms = parseUtcMs(iso)
  return ms === null ? NO_VALUE : new Date(ms).toISOString().slice(0, 19).replace('T', ' ')
}

/** YYYY-MM-DD HH:mm:ss.SSS（UTC） */
export function formatUtcDateTimeMs(iso: string | null | undefined): string {
  const ms = parseUtcMs(iso)
  return ms === null ? NO_VALUE : new Date(ms).toISOString().slice(0, 23).replace('T', ' ')
}

export function formatClock(ms: number): string {
  return new Date(ms).toISOString().slice(0, 19).replace('T', ' ')
}

/** 時間長度：<1s、12s、3m 05s、2h 10m、4d 03h。 */
export function formatDuration(deltaMs: number): string {
  const total = Math.max(0, Math.floor(deltaMs / 1000))
  if (total < 1) {
    return '<1s'
  }

  if (total < 60) {
    return `${total}s`
  }

  const minutes = Math.floor(total / 60)
  if (total < 3600) {
    return `${minutes}m ${String(total % 60).padStart(2, '0')}s`
  }

  const hours = Math.floor(total / 3600)
  if (total < 86_400) {
    return `${hours}h ${String(minutes % 60).padStart(2, '0')}m`
  }

  return `${Math.floor(total / 86_400)}d ${String(hours % 24).padStart(2, '0')}h`
}

/** 以 server 時間作基準計「幾耐之前」，避免 browser 同 server 時鐘有偏差而顯示錯誤嘅 age。 */
export function formatAgeBetween(fromIso: string | null | undefined, toIso: string | null | undefined): string {
  const from = parseUtcMs(fromIso)
  const to = parseUtcMs(toIso)
  return from === null || to === null ? NO_VALUE : formatDuration(to - from)
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) {
    return NO_VALUE
  }

  if (bytes < 1024) {
    return `${bytes} B`
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export function formatMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return NO_VALUE
  }

  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`
}

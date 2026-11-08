import type { PriceDto } from '../api/types'

export type FlashDirection = 'up' | 'down'

export interface Flash {
  direction: FlashDirection
  /** 偵測到變動嘅 poll 序號；用嚟做 React key，令連續兩次同方向變動都會重新播放動畫。 */
  seq: number
  atMs: number
}

const KEY_SEPARATOR = '\u001f'

export function rowKey(sourceId: string, instrumentId: string): string {
  return `${sourceId}${KEY_SEPARATOR}${instrumentId}`
}

export function buildBaseline(rows: readonly PriceDto[]): Map<string, number> {
  const baseline = new Map<string, number>()
  for (const row of rows) {
    baseline.set(rowKey(row.sourceId, row.instrumentId), row.price)
  }

  return baseline
}

export interface ComputeFlashesInput {
  /** 上一次畫面（同一個 query）嘅價格；null 代表冇可比較嘅畫面（第一次載入、換 filter／頁碼、斷線後重連），唔會有任何變動提示。 */
  baseline: ReadonlyMap<string, number> | null
  previous: ReadonlyMap<string, Flash>
  rows: readonly PriceDto[]
  nowMs: number
  seq: number
  holdMs: number
}

/**
 * 只比較「上一次畫面」同「今次畫面」之間嘅淨變化：兩次讀取之間如果價格 100 → 101 → 100，UI 睇唔到任何變動，
 * 亦唔會聲稱展示咗所有 tick。高亮喺 holdMs 內保留，慢過 poll 間隔嘅動畫唔會被截斷。
 */
export function computeFlashes({ baseline, previous, rows, nowMs, seq, holdMs }: ComputeFlashesInput): Map<string, Flash> {
  const flashes = new Map<string, Flash>()
  if (baseline === null) {
    return flashes
  }

  for (const row of rows) {
    const key = rowKey(row.sourceId, row.instrumentId)
    const before = baseline.get(key)
    if (before !== undefined && before !== row.price) {
      flashes.set(key, { direction: row.price > before ? 'up' : 'down', seq, atMs: nowMs })
      continue
    }

    const carried = previous.get(key)
    if (carried && nowMs - carried.atMs < holdMs) {
      flashes.set(key, carried)
    }
  }

  return flashes
}

/**
 * 冇新資料（request 失敗、backend 斷線）時，只清走已經過咗 holdMs 嘅高亮，
 * 避免舊嘅 ▲／▼ 一直留喺過時嘅畫面上。冇過期項目時原樣返回同一個 Map，唔會觸發多餘 re-render。
 */
export function pruneFlashes(
  flashes: ReadonlyMap<string, Flash>,
  nowMs: number,
  holdMs: number,
): ReadonlyMap<string, Flash> {
  const isFresh = (flash: Flash) => nowMs - flash.atMs < holdMs
  if ([...flashes.values()].every(isFresh)) {
    return flashes
  }

  return new Map([...flashes].filter(([, flash]) => isFresh(flash)))
}

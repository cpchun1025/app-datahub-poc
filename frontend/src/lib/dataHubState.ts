import type { ApiError, Settled } from '../api/client'
import type { HealthResponse, PriceListResponse, SourceListResponse } from '../api/types'
import { buildBaseline, computeFlashes, pruneFlashes, type Flash } from './flash'

export type ConnectionState = 'connecting' | 'connected' | 'disconnected'

/** 一輪 monitoring 或 prices 查詢；null 表示該資料集冇被請求。 */
export interface CycleResult {
  atMs: number
  /** 呢一輪 prices 所用嘅 query key。 */
  queryKey: string
  health: Settled<HealthResponse> | null
  sources: Settled<SourceListResponse> | null
  prices: Settled<PriceListResponse> | null
}

export interface DataHubErrors {
  health: ApiError | null
  sources: ApiError | null
  prices: ApiError | null
}

export interface DataHubState {
  connection: ConnectionState
  disconnectedSinceMs: number | null
  /** 最近一次成功更新 prices 嘅時間（UI refresh）。 */
  lastRefreshMs: number | null
  seq: number
  health: HealthResponse | null
  sources: SourceListResponse | null
  prices: PriceListResponse | null
  /** 目前 prices 係用邊個 query 讀嘅；同現行 query 唔同代表畫面仍然係上一個 query 嘅資料。 */
  pricesQueryKey: string | null
  baseline: ReadonlyMap<string, number> | null
  flashes: ReadonlyMap<string, Flash>
  errors: DataHubErrors
}

const NO_FLASHES: ReadonlyMap<string, Flash> = new Map()

export const initialDataHubState: DataHubState = {
  connection: 'connecting',
  disconnectedSinceMs: null,
  lastRefreshMs: null,
  seq: 0,
  health: null,
  sources: null,
  prices: null,
  pricesQueryKey: null,
  baseline: null,
  flashes: NO_FLASHES,
  errors: { health: null, sources: null, prices: null },
}

/**
 * 套用一輪 polling 結果（純函數，方便測試）。
 *
 * - 只有 monitoring cycle 改變連線狀態；price-only cycle 同未請求嘅資料集唔會掩蓋斷線。
 * - 每個資料集各自保留「最後一次成功」嘅結果，失敗唔會清走現有表格。
 * - 例外：prices 失敗而且 query 已經換咗，舊 query 嘅資料唔可以冒充新 query 嘅結果，所以清走。
 * - 價格變動只同「同一個 query、冇斷線」嘅上一個畫面比較；第一次載入、換 query、斷線重連後都唔會當成價格變動。
 */
export function applyCycle(state: DataHubState, cycle: CycleResult, holdMs: number): DataHubState {
  const monitoring = cycle.health !== null || cycle.sources !== null
  const reachable = [cycle.health, cycle.sources, cycle.prices].some((r) => r !== null && (r.ok || r.error.kind !== 'unreachable'))
  const connection = monitoring ? (reachable ? 'connected' : 'disconnected') : state.connection
  const seq = state.seq + 1

  let { prices, pricesQueryKey, baseline, flashes, lastRefreshMs } = state

  if (cycle.prices?.ok) {
    const comparable = state.pricesQueryKey === cycle.queryKey && state.connection !== 'disconnected'
    const items = cycle.prices.value.items
    flashes = computeFlashes({
      baseline: comparable ? state.baseline : null,
      previous: comparable ? state.flashes : NO_FLASHES,
      rows: items,
      nowMs: cycle.atMs,
      seq,
      holdMs,
    })
    baseline = buildBaseline(items)
    prices = cycle.prices.value
    pricesQueryKey = cycle.queryKey
    lastRefreshMs = cycle.atMs
  } else if (cycle.prices !== null && state.pricesQueryKey !== cycle.queryKey) {
    prices = null
    pricesQueryKey = null
    baseline = null
    flashes = NO_FLASHES
  } else {
    flashes = pruneFlashes(flashes, cycle.atMs, holdMs)
  }

  if (connection === 'disconnected') baseline = null

  return {
    connection,
    disconnectedSinceMs: connection !== 'disconnected' ? null : (state.disconnectedSinceMs ?? cycle.atMs),
    lastRefreshMs,
    seq,
    health: cycle.health?.ok ? cycle.health.value : state.health,
    sources: cycle.sources?.ok ? cycle.sources.value : state.sources,
    prices,
    pricesQueryKey,
    baseline,
    flashes,
    errors: {
      health: cycle.health === null ? state.errors.health : cycle.health.ok ? null : cycle.health.error,
      sources: cycle.sources === null ? state.errors.sources : cycle.sources.ok ? null : cycle.sources.error,
      prices: cycle.prices === null ? state.errors.prices : cycle.prices.ok ? null : cycle.prices.error,
    },
  }
}

/** backend 有回應但某個 request 失敗（validation、404、timeout…）時，畀 banner 顯示嘅第一個錯誤。 */
export function firstApiError(state: DataHubState): ApiError | null {
  if (state.connection !== 'connected') {
    return null
  }

  return state.errors.prices ?? state.errors.sources ?? state.errors.health
}

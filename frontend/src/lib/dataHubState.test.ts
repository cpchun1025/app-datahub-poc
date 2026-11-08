import { describe, expect, it } from 'vitest'
import { ApiError, type Settled } from '../api/client'
import type { HealthResponse, PriceDto, PriceListResponse, SourceListResponse } from '../api/types'
import { applyCycle, firstApiError, initialDataHubState, type CycleResult, type DataHubState } from './dataHubState'
import { rowKey } from './flash'

const HOLD_MS = 900

function price(value: number, instrumentId = 'A', sourceId = 's1'): PriceDto {
  return {
    sourceId,
    instrumentId,
    price: value,
    currency: 'USD',
    sourceUpdatedAtUtc: '2026-01-01T00:00:00Z',
    receivedAtUtc: '2026-01-01T00:00:00Z',
    sourceRevision: 1,
    sourceStatus: 'Live',
  }
}

function prices(...items: PriceDto[]): PriceListResponse {
  return { serverTimeUtc: '2026-01-01T00:00:00Z', page: 1, pageSize: 100, totalCount: items.length, totalPages: 1, items, sources: [] }
}

const health: HealthResponse = {
  status: 'ok',
  serverTimeUtc: '2026-01-01T00:00:00Z',
  startedAtUtc: '2026-01-01T00:00:00Z',
  mockMode: true,
  adminEnabled: true,
  sources: { total: 1, live: 1, initializing: 0, recovering: 0, stale: 0, disconnected: 0 },
  checkpoint: {
    enabled: true,
    loadedFrom: null,
    loadedSavedAtUtc: null,
    loadedRecordCount: null,
    loadWarnings: [],
    saveInProgress: false,
    lastAttemptAtUtc: null,
    lastSuccessAtUtc: null,
    lastSuccessRecordCount: null,
    lastSuccessBytes: null,
    lastSuccessDurationMs: null,
    lastErrorAtUtc: null,
    lastError: null,
    consecutiveFailures: 0,
  },
}

const sources: SourceListResponse = { serverTimeUtc: '2026-01-01T00:00:00Z', sources: [] }

const ok = <T,>(value: T): Settled<T> => ({ ok: true, value })
const unreachable = (): Settled<never> => ({ ok: false, error: new ApiError('unreachable', 'Failed to fetch') })
const problem = (status: number, title: string): Settled<never> => ({
  ok: false,
  error: new ApiError('problem', title, status, { title, status }),
})

function cycle(atMs: number, result: Partial<CycleResult> & { prices?: Settled<PriceListResponse> } = {}): CycleResult {
  return {
    atMs,
    queryKey: 'q1',
    health: ok(health),
    sources: ok(sources),
    prices: ok(prices(price(10))),
    ...result,
  }
}

function run(...cycles: CycleResult[]): DataHubState {
  return cycles.reduce((state, c) => applyCycle(state, c, HOLD_MS), initialDataHubState)
}

describe('applyCycle', () => {
  it('monitoring while paused retains rows but exposes disconnection and resets the flash baseline', () => {
    const initial = run(cycle(1000))
    const down = applyCycle(initial, {
      atMs: 2000, queryKey: '', health: unreachable(), sources: unreachable(), prices: null,
    }, HOLD_MS)
    expect(down.connection).toBe('disconnected')
    expect(down.prices).toBe(initial.prices)
    expect(down.pricesQueryKey).toBe('q1')
    expect(down.lastRefreshMs).toBe(1000)
    expect(down.baseline).toBeNull()
    const reconnected = applyCycle(down, { ...cycle(3000), prices: null }, HOLD_MS)
    const resumed = applyCycle(reconnected, {
      ...cycle(4000, { prices: ok(prices(price(99))) }), health: null, sources: null,
    }, HOLD_MS)
    expect(resumed.connection).toBe('connected')
    expect(resumed.flashes.size).toBe(0)
  })

  it('a price-only failure cannot override connectivity monitoring', () => {
    const state = applyCycle(run(cycle(1000)), {
      atMs: 2000, queryKey: 'q1', health: null, sources: null, prices: unreachable(),
    }, HOLD_MS)
    expect(state.connection).toBe('connected')
    expect(state.errors.prices?.kind).toBe('unreachable')
    expect(state.health).toBe(health)
  })

  it('starts as connecting with no data', () => {
    expect(initialDataHubState.connection).toBe('connecting')
    expect(initialDataHubState.prices).toBeNull()
  })

  it('the first successful load is connected and has no price flashes', () => {
    const state = run(cycle(1000))

    expect(state.connection).toBe('connected')
    expect(state.prices?.items).toHaveLength(1)
    expect(state.flashes.size).toBe(0)
    expect(state.lastRefreshMs).toBe(1000)
    expect(state.seq).toBe(1)
  })

  it('flags price changes between two reads of the same query', () => {
    const state = run(cycle(1000), cycle(2000, { prices: ok(prices(price(11))) }))

    expect(state.flashes.get(rowKey('s1', 'A'))?.direction).toBe('up')
  })

  it('drops expired flashes when later cycles fail, so a stale screen keeps no leftover arrows', () => {
    const down = { health: unreachable(), sources: unreachable(), prices: unreachable() }
    const flashed = run(cycle(1000), cycle(2000, { prices: ok(prices(price(11))) }))
    expect(flashed.flashes.size).toBe(1)

    // 高亮仍然喺 hold 時間內：保留；過咗 hold 之後嘅失敗輪次：清走，但表格資料保留。
    const withinHold = applyCycle(flashed, cycle(2500, down), HOLD_MS)
    expect(withinHold.flashes.size).toBe(1)

    const afterHold = applyCycle(withinHold, cycle(3000, down), HOLD_MS)
    expect(afterHold.flashes.size).toBe(0)
    expect(afterHold.prices?.items).toHaveLength(1)
  })

  it('does not flag a price change when the query changed (different rows on screen)', () => {
    const state = run(cycle(1000), cycle(2000, { queryKey: 'q2', prices: ok(prices(price(99))) }))

    expect(state.flashes.size).toBe(0)
    expect(state.pricesQueryKey).toBe('q2')
  })

  it('is disconnected only when every request is unreachable, and keeps the last data', () => {
    const state = run(
      cycle(1000),
      cycle(2000, { health: unreachable(), sources: unreachable(), prices: unreachable() }),
    )

    expect(state.connection).toBe('disconnected')
    expect(state.disconnectedSinceMs).toBe(2000)
    expect(state.prices?.items).toHaveLength(1)
    expect(state.health).toBe(health)
  })

  it('remembers when the disconnection started across repeated failures', () => {
    const down = { health: unreachable(), sources: unreachable(), prices: unreachable() }
    const state = run(cycle(1000), cycle(2000, down), cycle(3000, down), cycle(4000, down))

    expect(state.disconnectedSinceMs).toBe(2000)
  })

  it('treats a problem+json response as the backend being alive', () => {
    const state = run(
      cycle(1000),
      cycle(2000, { health: unreachable(), sources: unreachable(), prices: problem(400, 'One or more validation errors occurred.') }),
    )

    expect(state.connection).toBe('connected')
    expect(firstApiError(state)?.kind).toBe('problem')
    expect(state.prices?.items).toHaveLength(1)
  })

  it('does not show the previous query rows when the new query fails', () => {
    const state = run(cycle(1000), cycle(2000, { queryKey: 'q2', prices: problem(404, 'Unknown source') }))

    expect(state.prices).toBeNull()
    expect(state.pricesQueryKey).toBeNull()
    expect(firstApiError(state)?.message).toBe('Unknown source')
  })

  it('resets the flash baseline after reconnecting so stale values do not flash', () => {
    const down = { health: unreachable(), sources: unreachable(), prices: unreachable() }
    const state = run(cycle(1000), cycle(2000, down), cycle(3000, { prices: ok(prices(price(50))) }))

    expect(state.connection).toBe('connected')
    expect(state.disconnectedSinceMs).toBeNull()
    expect(state.flashes.size).toBe(0)

    const next = applyCycle(state, cycle(4000, { prices: ok(prices(price(51))) }), HOLD_MS)
    expect(next.flashes.get(rowKey('s1', 'A'))?.direction).toBe('up')
  })

  it('clears the error of a request once it succeeds again', () => {
    const state = run(cycle(1000, { prices: problem(500, 'boom') }), cycle(2000))

    expect(firstApiError(state)).toBeNull()
  })

  it('does not report API errors while disconnected', () => {
    const state = run(cycle(1000, { health: unreachable(), sources: unreachable(), prices: unreachable() }))

    expect(state.connection).toBe('disconnected')
    expect(firstApiError(state)).toBeNull()
  })

  it('retains health and sources when only prices succeeds', () => {
    const state = run(cycle(1000), cycle(2000, { health: unreachable(), sources: unreachable() }))

    expect(state.connection).toBe('connected')
    expect(state.health).toBe(health)
    expect(state.sources).toBe(sources)
    expect(state.errors.health?.kind).toBe('unreachable')
  })
})

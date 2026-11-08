import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PriceQuery } from '../api/client'
import { createPoller } from './poller'
import { createPricePoller } from './pricePoller'

const query: PriceQuery = { sourceId: null, instrument: '', page: 1, pageSize: 25 }
function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>(r => { resolve = r })
  return { promise, resolve }
}

describe('price display polling', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('pause cancels pending prices but connectivity monitoring continues', async () => {
    const run = vi.fn(async () => {})
    const monitorRun = vi.fn(async () => {})
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    const monitor = createPoller({ intervalMs: 1000, run: monitorRun })
    monitor.start()
    display.update(query, false)
    await vi.advanceTimersByTimeAsync(0)
    display.update(query, true)
    await vi.advanceTimersByTimeAsync(3000)
    expect(run).toHaveBeenCalledTimes(1)
    expect(monitorRun).toHaveBeenCalledTimes(4)
    display.stop()
    monitor.stop()
  })

  it('a query change while paused runs once; resume refreshes immediately then polls', async () => {
    const run = vi.fn(async (_query: PriceQuery, _signal: AbortSignal) => {})
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, true)
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
    const next = { ...query, currency: 'USD', sortBy: 'price' as const, page: 2 }
    display.update(next, true)
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(2)
    expect(run.mock.calls[1]?.[0]).toEqual(next)
    display.update(next, false)
    expect(run).toHaveBeenCalledTimes(3)
    await vi.advanceTimersByTimeAsync(1000)
    expect(run).toHaveBeenCalledTimes(4)
    display.stop()
  })

  it('aborts old queries and never overlaps even if transport ignores abort until it settles', async () => {
    const gate = deferred()
    let active = 0
    let maxActive = 0
    const applied: number[] = []
    const signals: AbortSignal[] = []
    const run = vi.fn(async (q: PriceQuery, signal: AbortSignal) => {
      signals.push(signal)
      maxActive = Math.max(maxActive, ++active)
      if (q.page === 1) await gate.promise
      if (!signal.aborted) applied.push(q.page)
      active--
    })
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, false)
    display.update({ ...query, page: 2 }, false)
    display.update({ ...query, page: 3 }, true)
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
    expect(signals[0]?.aborted).toBe(true)
    gate.resolve()
    await vi.advanceTimersByTimeAsync(0)
    expect(applied).toEqual([3])
    expect(maxActive).toBe(1)
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(2)
    display.stop()
  })

  it('pausing an in-flight request discards it and schedules no replacement', async () => {
    const gate = deferred()
    let signal: AbortSignal | undefined
    const run = vi.fn(async (_q: PriceQuery, s: AbortSignal) => { signal = s; await gate.promise })
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, false)
    display.update(query, true)
    expect(signal?.aborted).toBe(true)
    gate.resolve()
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
    display.stop()
  })

  it('manual refresh while paused runs once without resuming', async () => {
    const run = vi.fn(async () => {})
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, true)
    await vi.advanceTimersByTimeAsync(0)
    display.refresh()
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(2)
    display.stop()
  })

  it('equivalent query objects do not cause additional reads', async () => {
    const run = vi.fn(async () => {})
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, true)
    await vi.advanceTimersByTimeAsync(0)
    display.update({ ...query }, true)
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
    display.stop()
  })

  it('reports errors; does not automatically retry paused reads', async () => {
    const error = new Error('failed')
    const run = vi.fn(async () => { throw error })
    const onError = vi.fn()
    const display = createPricePoller({ intervalMs: 1000, run, onError })
    display.update(query, true)
    await vi.advanceTimersByTimeAsync(5000)
    expect(onError).toHaveBeenCalledExactlyOnceWith(error, query)
    display.update(query, false)
    await vi.advanceTimersByTimeAsync(1000)
    expect(run).toHaveBeenCalledTimes(3)
    display.stop()
  })

  it('stop aborts and permanently disables timers, refresh and updates', async () => {
    const gate = deferred()
    const run = vi.fn(async () => gate.promise)
    const display = createPricePoller({ intervalMs: 1000, run, onError: vi.fn() })
    display.update(query, false)
    display.stop()
    display.refresh()
    display.update({ ...query, page: 2 }, false)
    gate.resolve()
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
    expect(vi.getTimerCount()).toBe(0)
  })
})

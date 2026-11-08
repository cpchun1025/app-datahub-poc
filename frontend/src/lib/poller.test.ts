import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPoller } from './poller'

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>((r) => {
    resolve = r
  })
  return { promise, resolve }
}

describe('createPoller', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('runs immediately and then waits the interval after each run completes', async () => {
    const run = vi.fn(async () => {})
    const poller = createPoller({ intervalMs: 1000, run })

    poller.start()
    expect(run).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(999)
    expect(run).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(1)
    expect(run).toHaveBeenCalledTimes(2)

    poller.stop()
  })

  it('never overlaps runs when a run is slower than the interval', async () => {
    const gate = deferred()
    let active = 0
    let maxActive = 0
    const run = vi.fn(async () => {
      active++
      maxActive = Math.max(maxActive, active)
      await gate.promise
      active--
    })
    const poller = createPoller({ intervalMs: 1000, run })

    poller.start()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(run).toHaveBeenCalledTimes(1)

    gate.resolve()
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(1000)
    expect(run).toHaveBeenCalledTimes(2)
    expect(maxActive).toBe(1)

    poller.stop()
  })

  it('aborts the in-flight run on stop and never starts another cycle', async () => {
    let seen: AbortSignal | undefined
    const run = vi.fn(
      (signal: AbortSignal) =>
        new Promise<void>((_, reject) => {
          seen = signal
          signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))
        }),
    )
    const onError = vi.fn()
    const poller = createPoller({ intervalMs: 1000, run, onError })

    poller.start()
    expect(seen?.aborted).toBe(false)

    poller.stop()
    expect(seen?.aborted).toBe(true)

    await vi.advanceTimersByTimeAsync(10_000)
    expect(run).toHaveBeenCalledTimes(1)
    expect(onError).not.toHaveBeenCalled()
  })

  it('clears the pending timer on stop', async () => {
    const run = vi.fn(async () => {})
    const poller = createPoller({ intervalMs: 1000, run })

    poller.start()
    await vi.advanceTimersByTimeAsync(0)
    poller.stop()

    await vi.advanceTimersByTimeAsync(10_000)
    expect(run).toHaveBeenCalledTimes(1)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('reports run errors and keeps polling', async () => {
    const boom = new Error('boom')
    const run = vi.fn<() => Promise<void>>().mockRejectedValueOnce(boom).mockResolvedValue(undefined)
    const onError = vi.fn()
    const poller = createPoller({ intervalMs: 500, run, onError })

    poller.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(onError).toHaveBeenCalledExactlyOnceWith(boom)

    await vi.advanceTimersByTimeAsync(500)
    expect(run).toHaveBeenCalledTimes(2)

    poller.stop()
  })

  it('is single-use: a second start or a start after stop does nothing', async () => {
    const run = vi.fn(async () => {})
    const poller = createPoller({ intervalMs: 1000, run })

    poller.start()
    poller.start()
    expect(run).toHaveBeenCalledTimes(1)

    poller.stop()
    poller.start()
    await vi.advanceTimersByTimeAsync(5000)
    expect(run).toHaveBeenCalledTimes(1)
  })
})

import { priceQueryKey, type PriceQuery } from '../api/client'

interface PricePollerOptions {
  intervalMs: number
  run: (query: PriceQuery, signal: AbortSignal) => Promise<void>
  onError: (error: unknown, query: PriceQuery) => void
}

export interface PricePoller {
  update(query: PriceQuery, paused: boolean): void
  refresh(): void
  stop(): void
}

/** A single price request at a time, including across cancellation/query changes. Monitoring is separate. */
export function createPricePoller({ intervalMs, run, onError }: PricePollerOptions): PricePoller {
  let query: PriceQuery | null = null
  let paused = false
  let stopped = false
  let pending = false
  let active = false
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined

  function clearTimer() {
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
  }

  async function cycle(): Promise<void> {
    if (stopped || active || query === null || (paused && !pending)) return
    clearTimer()
    active = true
    pending = false
    const currentQuery = query
    const current = new AbortController()
    controller = current
    try {
      await run(currentQuery, current.signal)
    } catch (error) {
      if (!current.signal.aborted) onError(error, currentQuery)
    } finally {
      active = false
      controller = undefined
      if (!stopped) {
        if (pending) void cycle()
        else if (!paused) timer = setTimeout(() => void cycle(), intervalMs)
      }
    }
  }

  return {
    update(nextQuery, nextPaused) {
      if (stopped) return
      const changed = query === null || priceQueryKey(query) !== priceQueryKey(nextQuery)
      const resumed = paused && !nextPaused
      const pauseStarted = !paused && nextPaused
      query = nextQuery
      paused = nextPaused
      if (changed || resumed) {
        pending = true
        clearTimer()
        controller?.abort()
        void cycle()
      } else if (pauseStarted) {
        pending = false
        clearTimer()
        controller?.abort()
      }
    },
    refresh() {
      if (stopped) return
      pending = true
      clearTimer()
      controller?.abort()
      void cycle()
    },
    stop() {
      stopped = true
      clearTimer()
      controller?.abort()
    },
  }
}

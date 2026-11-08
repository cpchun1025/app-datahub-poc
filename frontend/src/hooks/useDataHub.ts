import { useCallback, useEffect, useReducer, useRef } from 'react'
import { ApiError, DEFAULT_TIMEOUT_MS, api, priceQueryKey, settle, type PriceQuery } from '../api/client'
import { config } from '../config'
import { applyCycle, initialDataHubState, type CycleResult, type DataHubState } from '../lib/dataHubState'
import { createPoller } from '../lib/poller'
import { createPricePoller, type PricePoller } from '../lib/pricePoller'

function reduce(state: DataHubState, cycle: CycleResult): DataHubState {
  return applyCycle(state, cycle, config.flashHoldMs)
}

/** Connectivity continues independently while the price display is paused. */
export function useDataHub(query: PriceQuery, pollIntervalMs: number, paused = false) {
  const [state, dispatch] = useReducer(reduce, initialDataHubState)
  const pricePoller = useRef<PricePoller | null>(null)

  useEffect(() => {
    const timeoutMs = Math.max(DEFAULT_TIMEOUT_MS, pollIntervalMs * 3)
    const monitor = createPoller({
      intervalMs: pollIntervalMs,
      run: async (signal) => {
        const [health, sources] = await Promise.all([
          settle(api.health(signal, timeoutMs), signal),
          settle(api.sources(signal, timeoutMs), signal),
        ])
        if (!signal.aborted) {
          dispatch({ atMs: Date.now(), queryKey: '', health, sources, prices: null })
        }
      },
      onError: (error) => console.error('Connectivity polling failed', error),
    })
    const display = createPricePoller({
      intervalMs: pollIntervalMs,
      run: async (current, signal) => {
        const prices = await settle(api.prices(current, signal, timeoutMs), signal)
        if (!signal.aborted) {
          dispatch({ atMs: Date.now(), queryKey: priceQueryKey(current), health: null, sources: null, prices })
        }
      },
      onError: (error, current) => dispatch({
        atMs: Date.now(), queryKey: priceQueryKey(current), health: null, sources: null,
        prices: { ok: false, error: new ApiError('http', error instanceof Error ? error.message : 'Price polling failed') },
      }),
    })
    pricePoller.current = display
    monitor.start()
    return () => {
      monitor.stop()
      display.stop()
      pricePoller.current = null
    }
  }, [pollIntervalMs])

  useEffect(() => {
    pricePoller.current?.update(query, paused)
  }, [query, paused, pollIntervalMs])

  /** 即刻重新讀取一次（例如手動 action 完成之後），取代等下一個 polling 週期。 */
  const refreshNow = useCallback(() => pricePoller.current?.refresh(), [])

  return { state, refreshNow }
}

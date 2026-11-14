import { useCallback, useEffect, useRef, useState } from 'react'
import { api, describeApiError } from '../api/client'
import type { CheckpointRunResponse, MockOutageResponse, SnapshotRefreshResponse } from '../api/types'
import { formatBytes, formatInteger, formatMs, formatUtcTime } from '../lib/format'

export type ActionKind = 'snapshot' | 'checkpoint' | 'outage'

export interface ActionResult {
  ok: boolean
  text: string
  atMs: number
}

interface ActionOutcome {
  ok: boolean
  text: string
}

const RESULT_TTL_MS = 15_000

export function describeSnapshot(result: SnapshotRefreshResponse): string {
  const base =
    `${result.sourceId}: +${result.added} added, ${result.updated} updated, ${result.removed} removed, ` +
    `${result.keptNewer} kept newer (${formatInteger(result.totalRecords)} records)`
  return result.skipped ? `${base} — skipped (older than the data already held)` : base
}

export function describeCheckpoint(result: CheckpointRunResponse): string {
  const c = result.checkpoint
  return (
    `${result.outcome}: ${formatInteger(c.lastSuccessRecordCount)} records, ` +
    `${formatBytes(c.lastSuccessBytes)}, ${formatMs(c.lastSuccessDurationMs)}`
  )
}

export function describeOutage(result: MockOutageResponse): string {
  return result.seconds === 0
    ? `${result.sourceId}: outage ended`
    : `${result.sourceId}: simulated outage for ${result.seconds}s (until ${formatUtcTime(result.outageUntilUtc)} UTC)`
}

/**
 * 手動 admin action（snapshot／checkpoint／outage）。同一時間只跑一個 action；
 * unmount 時取消進行中嘅 request，完成後通知 caller 即刻重新讀取畫面資料。
 */
export function useAdminActions(onSettled: () => void) {
  const [busy, setBusy] = useState<ActionKind | null>(null)
  const [result, setResult] = useState<ActionResult | null>(null)
  const controllerRef = useRef<AbortController | null>(null)

  useEffect(() => () => controllerRef.current?.abort(), [])

  useEffect(() => {
    if (!result) {
      return
    }

    const id = setTimeout(() => setResult((current) => (current === result ? null : current)), RESULT_TTL_MS)
    return () => clearTimeout(id)
  }, [result])

  const run = useCallback(
    async (kind: ActionKind, work: (signal: AbortSignal) => Promise<ActionOutcome>) => {
      if (controllerRef.current) {
        return
      }

      const controller = new AbortController()
      controllerRef.current = controller
      setBusy(kind)

      try {
        const outcome = await work(controller.signal)
        if (!controller.signal.aborted) {
          setResult({ ...outcome, atMs: Date.now() })
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setResult({ ok: false, text: describeApiError(error), atMs: Date.now() })
        }
      } finally {
        controllerRef.current = null
        if (!controller.signal.aborted) {
          setBusy(null)
          onSettled()
        }
      }
    },
    [onSettled],
  )

  const refreshSnapshots = useCallback(
    (sourceIds: readonly string[]) =>
      run('snapshot', async (signal) => {
        if (sourceIds.length === 0) {
          return { ok: false, text: 'No source to refresh.' }
        }

        const outcomes = await Promise.allSettled(sourceIds.map((id) => api.refreshSnapshot(id, signal)))
        const lines = outcomes.map((outcome, index) =>
          outcome.status === 'fulfilled'
            ? describeSnapshot(outcome.value)
            : `${sourceIds[index]}: FAILED — ${describeApiError(outcome.reason)}`,
        )

        return { ok: outcomes.every((o) => o.status === 'fulfilled'), text: `SNAPSHOT ${lines.join(' | ')}` }
      }),
    [run],
  )

  const saveCheckpoint = useCallback(
    () =>
      run('checkpoint', async (signal) => ({
        ok: true,
        text: `CHECKPOINT ${describeCheckpoint(await api.saveCheckpoint(signal))}`,
      })),
    [run],
  )

  const simulateOutage = useCallback(
    (sourceId: string, seconds: number) =>
      run('outage', async (signal) => ({
        ok: true,
        text: `OUTAGE ${describeOutage(await api.simulateOutage(sourceId, seconds, signal))}`,
      })),
    [run],
  )

  const simulateNewsOutage = useCallback(
    (seconds: number) => run('outage', async (signal) => ({
      ok: true,
      text: `OUTAGE ${describeOutage(await api.simulateNewsOutage(seconds, signal))}`,
    })),
    [run],
  )

  return { busy, result, refreshSnapshots, saveCheckpoint, simulateOutage, simulateNewsOutage }
}

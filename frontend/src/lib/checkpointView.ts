import type { CheckpointDto } from '../api/types'
import { formatBytes, formatInteger, formatMs, formatUtcDateTime, formatUtcTime } from './format'

export type CheckpointTone = 'ok' | 'dim' | 'busy' | 'error'

export interface CheckpointView {
  tone: CheckpointTone
  label: string
  detail: string
}

/** status bar 嘅 checkpoint 摘要。backend 成功儲存後會清走 lastError，所以有 lastError 就代表最近一次失敗。 */
export function checkpointView(checkpoint: CheckpointDto | null): CheckpointView {
  if (checkpoint === null) {
    return { tone: 'dim', label: 'UNKNOWN', detail: 'Waiting for the backend.' }
  }

  if (!checkpoint.enabled) {
    return {
      tone: 'dim',
      label: 'DISABLED',
      detail: 'Checkpointing is off (DataHub:Checkpoint:Enabled=false); a restart begins with an empty store.',
    }
  }

  if (checkpoint.consecutiveFailures > 0 || checkpoint.lastError) {
    const lastOk = checkpoint.lastSuccessAtUtc ? ` · last OK ${formatUtcTime(checkpoint.lastSuccessAtUtc)} UTC` : ' · never saved OK'
    return {
      tone: 'error',
      label: `ERROR ×${Math.max(1, checkpoint.consecutiveFailures)}`,
      detail: `${checkpoint.lastError ?? 'Checkpoint save failed'}${lastOk}`,
    }
  }

  if (checkpoint.saveInProgress) {
    return { tone: 'busy', label: 'SAVING…', detail: 'A checkpoint save is running.' }
  }

  if (checkpoint.lastSuccessAtUtc) {
    return {
      tone: 'ok',
      label: 'OK',
      detail:
        `${formatUtcTime(checkpoint.lastSuccessAtUtc)} UTC · ${formatInteger(checkpoint.lastSuccessRecordCount)} records` +
        ` · ${formatBytes(checkpoint.lastSuccessBytes)} · ${formatMs(checkpoint.lastSuccessDurationMs)}`,
    }
  }

  return { tone: 'dim', label: 'NONE YET', detail: 'No checkpoint saved since this process started.' }
}

/** 啟動時由 checkpoint 還原嘅摘要；冇還原過就回傳 null。 */
export function checkpointLoadSummary(checkpoint: CheckpointDto | null): string | null {
  if (checkpoint === null || !checkpoint.loadedFrom) {
    return null
  }

  const warnings = checkpoint.loadWarnings.length > 0 ? ` · ${checkpoint.loadWarnings.length} warning(s)` : ''
  return (
    `Restored at startup from the ${checkpoint.loadedFrom} file (saved ${formatUtcDateTime(checkpoint.loadedSavedAtUtc)} UTC, ` +
    `${formatInteger(checkpoint.loadedRecordCount)} records)${warnings}`
  )
}

import type { SourceStatus } from '../api/types'

/** 每個狀態有獨立形狀（glyph）同文字，唔係淨靠顏色分辨。 */
export type StatusTone = 'live' | 'init' | 'recovering' | 'stale' | 'down'

export interface StatusInfo {
  label: string
  tone: StatusTone
  hint: string
}

const STATUS_INFO: Record<SourceStatus, StatusInfo> = {
  Live: {
    label: 'LIVE',
    tone: 'live',
    hint: 'Synced since this process started, feed connected and fresh.',
  },
  Initializing: {
    label: 'INIT',
    tone: 'init',
    hint: 'Waiting for the first snapshot; no data from this source is trusted yet.',
  },
  Recovering: {
    label: 'RECOVERING',
    tone: 'recovering',
    hint: 'Data restored from a checkpoint and/or resyncing. Values may be outdated until a fresh snapshot is applied.',
  },
  Stale: {
    label: 'STALE',
    tone: 'stale',
    hint: 'No feed activity within the freshness window; values may be outdated.',
  },
  Disconnected: {
    label: 'DISCONNECTED',
    tone: 'down',
    hint: 'The source feed or snapshot is failing; values are the last known state.',
  },
}

export function statusInfo(status: string): StatusInfo {
  return (
    (STATUS_INFO as Record<string, StatusInfo | undefined>)[status] ?? {
      label: status.toUpperCase(),
      tone: 'init',
      hint: 'Unrecognised status reported by the backend.',
    }
  )
}

export const BACKEND_UNREACHABLE_INFO: StatusInfo = {
  label: 'DISCONNECTED',
  tone: 'down',
  hint: 'Backend unreachable; the status shown here cannot be confirmed.',
}

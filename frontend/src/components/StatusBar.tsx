import type { ApiError } from '../api/client'
import type { HealthResponse } from '../api/types'
import { checkpointLoadSummary, checkpointView } from '../lib/checkpointView'
import { formatClock, formatInteger } from '../lib/format'

interface StatusBarProps {
  lastRefreshMs: number | null
  pollIntervalMs: number
  health: HealthResponse | null
  backendDown: boolean
  totalCount: number | null
  errors: { health: ApiError | null; sources: ApiError | null; prices: ApiError | null }
  sourceErrors: readonly string[]
  paused: boolean
}

function errorSummary({ backendDown, errors, sourceErrors, health }: StatusBarProps): string {
  const parts: string[] = []
  if (backendDown) {
    parts.push('backend unreachable')
  } else {
    if (errors.health) {
      parts.push(`health: ${errors.health.message}`)
    }

    if (errors.sources) {
      parts.push(`sources: ${errors.sources.message}`)
    }

    if (errors.prices) {
      parts.push(`prices: ${errors.prices.message}`)
    }
  }

  if (health?.checkpoint.lastError) {
    parts.push('checkpoint save failing')
  }

  if (sourceErrors.length > 0) {
    parts.push(`source errors: ${sourceErrors.join(', ')}`)
  }

  return parts.length === 0 ? 'NONE' : parts.join(' | ')
}

export function StatusBar(props: StatusBarProps) {
  const { lastRefreshMs, pollIntervalMs, health, totalCount } = props
  const checkpoint = checkpointView(health?.checkpoint ?? null)
  const loadSummary = checkpointLoadSummary(health?.checkpoint ?? null)
  const errors = errorSummary(props)

  return (
    <footer className="statusbar">
      <span className="sb-item" title={props.paused ? 'Price display paused; connectivity monitoring continues.' : `Polling every ${pollIntervalMs} ms. The screen shows net change between two reads, not every tick.`}>
        <span className="k">UI REFRESH</span> {lastRefreshMs === null ? '—' : `${formatClock(lastRefreshMs)} UTC`}
        <span className="dim"> · {props.paused ? 'DISPLAY PAUSED' : `every ${pollIntervalMs} ms`}</span>
      </span>
      {totalCount !== null ? (
        <span className="sb-item">
          <span className="k">ROWS</span> {formatInteger(totalCount)}
        </span>
      ) : null}
      <span className="sb-item" title={loadSummary ?? undefined}>
        <span className="k">CHECKPOINT</span> <span className={`cp cp-${checkpoint.tone}`}>{checkpoint.label}</span>
        {checkpoint.detail ? <span className="dim"> {checkpoint.detail}</span> : null}
        {loadSummary ? <span className="dim"> · restored at startup</span> : null}
      </span>
      <span className={`sb-item sb-errors${errors === 'NONE' ? '' : ' has-errors'}`} title={errors}>
        <span className="k">ERRORS</span> {errors}
      </span>
    </footer>
  )
}

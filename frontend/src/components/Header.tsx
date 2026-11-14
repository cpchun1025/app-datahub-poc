import type { ConnectionState } from '../lib/dataHubState'
import { formatUtcTime } from '../lib/format'
import type { StatusTone } from '../lib/status'
import { Clock } from './Clock'
import { StatusGlyph } from './StatusGlyph'

interface HeaderProps {
  connection: ConnectionState
  hasApiError: boolean
  mockMode: boolean | null
  lastResponseMs: number | null
  disconnectedSinceMs: number | null
}

function connectionView(connection: ConnectionState, hasApiError: boolean): { tone: StatusTone; label: string } {
  if (connection === 'connecting') {
    return { tone: 'init', label: 'CONNECTING…' }
  }

  if (connection === 'disconnected') {
    return { tone: 'down', label: 'BACKEND DISCONNECTED' }
  }

  return hasApiError ? { tone: 'stale', label: 'BACKEND API ERROR' } : { tone: 'live', label: 'BACKEND CONNECTED' }
}

export function Header({ connection, hasApiError, mockMode, lastResponseMs, disconnectedSinceMs }: HeaderProps) {
  const view = connectionView(connection, hasApiError)
  const detail =
    connection === 'disconnected' && disconnectedSinceMs !== null
      ? `Unreachable since ${formatUtcTime(new Date(disconnectedSinceMs).toISOString())} UTC`
      : lastResponseMs !== null
        ? `Last data ${formatUtcTime(new Date(lastResponseMs).toISOString())} UTC`
        : 'Waiting for the first response'

  return (
    <header className="hdr">
      <h1 className="hdr-title">DATA HUB</h1>
      <span className="hdr-sub">MULTI-MODEL POC</span>
      {mockMode ? (
        <span className="badge badge-mock" title="All sources are simulated. No real external service is connected.">
          MOCK
        </span>
      ) : null}
      <output className={`status status-${view.tone}`} title={detail}>
        <StatusGlyph tone={view.tone} />
        <span>{view.label}</span>
      </output>
      <span className="hdr-spacer" />
      <Clock />
    </header>
  )
}

import type { ActionKind, ActionResult } from '../hooks/useAdminActions'
import { formatInteger, formatUtcDateTime } from '../lib/format'
import { BACKEND_UNREACHABLE_INFO, type StatusInfo } from '../lib/status'
import { StatusBadge } from './StatusBadge'

export interface NewsSourceStatus {
  sourceId: string
  state: string
  lastAttemptAtUtc: string | null
  lastSuccessfulFetchAtUtc: string | null
  latestArticlePublishedAtUtc: string | null
  lastError: string | null
  cachedItemCount: number
}

const NEWS_STATUS: Record<string, StatusInfo> = {
  Connected: { label: 'CONNECTED', tone: 'live', hint: 'The latest news fetch succeeded.' },
  Initializing: { label: 'INITIALIZING', tone: 'init', hint: 'Waiting for the first news fetch.' },
  Recovering: { label: 'RECOVERING', tone: 'recovering', hint: 'Cached news restored; waiting for a successful fetch.' },
  Disconnected: { label: 'DISCONNECTED', tone: 'down', hint: 'The latest news fetch failed; cached articles are retained.' },
}

export function NewsSourcePanel({ source, unavailable, adminEnabled, busy, actionResult, onOutage }: {
  source: NewsSourceStatus | null
  unavailable: boolean
  adminEnabled: boolean
  busy: ActionKind | null
  actionResult: ActionResult | null
  onOutage: (seconds: number) => void
}) {
  const info = source ? NEWS_STATUS[source.state] ?? {
    label: source.state.toUpperCase(), tone: 'init' as const, hint: 'Unrecognised news source status.',
  } : null
  const disabled = unavailable || !adminEnabled || busy !== null
  return (
    <section className="panel news-sources" aria-labelledby="news-sources-title">
      <h2 id="news-sources-title" className="panel-title">NEWS SOURCES
        <span className="panel-note">Fetch health is separate from article publication time</span>
      </h2>
      <div className="panel-scroll">
        <table className="grid grid-sources">
          <thead><tr>
            <th scope="col">STATUS</th><th scope="col">SOURCE</th><th scope="col">CACHE</th>
            <th scope="col">LAST ATTEMPT UTC</th><th scope="col">LAST SUCCESS UTC</th>
            <th scope="col">LATEST PUBLICATION UTC</th><th scope="col">NOTES</th><th scope="col">ACTIONS</th>
          </tr></thead>
          <tbody>{source && info ? <tr>
            <td data-label="STATUS"><StatusBadge info={unavailable ? BACKEND_UNREACHABLE_INFO : info}
              title={unavailable ? 'News API unavailable; last known source status cannot be confirmed.' : undefined} /></td>
            <td data-label="SOURCE">{source.sourceId} <span className="badge badge-mock badge-inline">MOCK</span></td>
            <td data-label="CACHE">{formatInteger(source.cachedItemCount)}</td>
            <td data-label="LAST ATTEMPT UTC">{formatUtcDateTime(source.lastAttemptAtUtc)}</td>
            <td data-label="LAST SUCCESS UTC">{formatUtcDateTime(source.lastSuccessfulFetchAtUtc)}</td>
            <td data-label="LATEST PUBLICATION UTC">{formatUtcDateTime(source.latestArticlePublishedAtUtc)}</td>
            <td data-label="NOTES">{source.lastError ? <span className="err">{source.lastError}</span> : '—'}</td>
            <td data-label="ACTIONS" className="news-source-actions">
              <button type="button" className="btn btn-small" disabled={disabled}
                title="Simulate a 20-second news fetch outage; recovery occurs on the next fetch."
                onClick={() => onOutage(20)}>Outage 20s</button>
              <button type="button" className="btn btn-small" disabled={disabled}
                title="End the simulated outage; source status updates after the next fetch."
                onClick={() => onOutage(0)}>End outage</button>
            </td>
          </tr> : <tr><td colSpan={8} className="empty">
            {unavailable ? 'News status unavailable.' : 'Waiting for news source status…'}
          </td></tr>}</tbody>
        </table>
      </div>
      <output className={`action-msg${actionResult ? (actionResult.ok ? ' ok' : ' err') : ''}`}>
        {actionResult ? <><strong>{actionResult.ok ? 'OK' : 'ERROR'}</strong> {actionResult.text}</> : null}
      </output>
    </section>
  )
}

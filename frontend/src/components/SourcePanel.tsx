import { useMemo, useState } from 'react'
import type { SourceDto } from '../api/types'
import type { ActionKind } from '../hooks/useAdminActions'
import { formatAgeBetween, formatInteger, formatUtcTime } from '../lib/format'
import { filterSources } from '../lib/sourceFilters'
import { BACKEND_UNREACHABLE_INFO, statusInfo } from '../lib/status'
import { StatusBadge } from './StatusBadge'

const OUTAGE_SECONDS = 20
const EMPTY_SOURCES: readonly SourceDto[] = []

interface SourcePanelProps {
  sources: readonly SourceDto[] | null
  serverTimeUtc: string | null
  backendDown: boolean
  sourceFilter: string | null
  onToggleSource: (sourceId: string) => void
  adminEnabled: boolean | null
  busy: ActionKind | null
  onOutage: (sourceId: string, seconds: number) => void
}

function notes(source: SourceDto): string[] {
  const result: string[] = []
  if (source.snapshotInProgress) {
    result.push('SNAPSHOT RUNNING')
  }

  if (source.restoredFromCheckpoint) {
    result.push(source.syncedSinceStart ? 'RESTORED → RESYNCED' : 'RESTORED, AWAITING SNAPSHOT')
  }

  return result
}

function timeWithAge(iso: string | null, serverTimeUtc: string | null): string {
  return iso ? `${formatUtcTime(iso)} (${formatAgeBetween(iso, serverTimeUtc)})` : '—'
}

export function SourcePanel(props: SourcePanelProps) {
  const { sources, serverTimeUtc, backendDown, sourceFilter } = props
  const [expanded, setExpanded] = useState(false)
  const [search, setSearch] = useState('')
  const [kind, setKind] = useState('')
  const [status, setStatus] = useState('')
  const outageDisabled = backendDown || props.adminEnabled !== true || props.busy !== null
  const items = sources ?? EMPTY_SOURCES
  const errors = items.filter((source) => source.lastError || (source.csvImport?.errorCount ?? 0) > 0).length
  const filteredSources = useMemo(() => filterSources(items, search, kind, status), [items, search, kind, status])

  return (
    <section className="panel sources" aria-labelledby="sources-title">
      <div className="panel-title source-panel-heading">
        <h2 id="sources-title">SOURCE MONITORING</h2>
        <span className="source-summary" aria-live="polite">
          {formatInteger(items.length)} total · {items.filter((item) => item.status === 'Live').length} LIVE ·
          {' '}{items.filter((item) => item.status === 'Initializing').length} INITIALIZING ·
          {' '}{items.filter((item) => item.status === 'Recovering').length} RECOVERING ·
          {' '}{items.filter((item) => item.status === 'Stale').length} STALE ·
          {' '}{items.filter((item) => item.status === 'Disconnected').length} DISCONNECTED
          {errors > 0 ? <strong className="err"> · {errors} WARNING{errors === 1 ? '' : 'S'}</strong> : null}
        </span>
        <button
          type="button"
          className="btn btn-small source-toggle"
          aria-expanded={expanded}
          aria-controls="source-monitor-content"
          onClick={() => setExpanded((value) => !value)}
        >
          {expanded ? 'Collapse sources' : 'Expand sources'}
        </button>
      </div>
      {expanded ? <div id="source-monitor-content" className="source-monitor-content">
        <div className="source-monitor-filters" aria-label="Source monitoring filters">
          <label>
            Search sources
            <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Name or source ID" />
          </label>
          <label>
            Adapter kind
            <select value={kind} onChange={(event) => setKind(event.target.value)}>
              <option value="">All kinds</option>
              {[...new Set(items.map((source) => source.kind))].sort().map((sourceKind) =>
                <option key={sourceKind} value={sourceKind}>{sourceKind.toUpperCase()}</option>)}
            </select>
          </label>
          <label>
            Health status
            <select value={status} onChange={(event) => setStatus(event.target.value)}>
              <option value="">All statuses</option>
              {['Live', 'Initializing', 'Recovering', 'Stale', 'Disconnected'].map((sourceStatus) =>
                <option key={sourceStatus} value={sourceStatus}>{sourceStatus}</option>)}
            </select>
          </label>
          <span className="source-filter-count" aria-live="polite">
            Showing {filteredSources.length} of {items.length} sources
          </span>
        </div>
      <div className="panel-scroll">
        <table className="grid grid-sources">
          <thead>
            <tr>
              <th scope="col">STATUS</th>
              <th scope="col">SOURCE</th>
              <th scope="col">KIND</th>
              <th scope="col" className="num">
                RECORDS
              </th>
              <th scope="col">LAST SNAPSHOT</th>
              <th scope="col">LAST UPDATE</th>
              <th scope="col">NEXT SNAPSHOT</th>
              <th scope="col">NOTES</th>
              <th scope="col">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {sources === null ? (
              <tr>
                <td colSpan={9} className="empty">
                  Waiting for the first response…
                </td>
              </tr>
            ) : filteredSources.length === 0 ? (
              <tr>
                <td colSpan={9} className="empty">
                  The backend reports no sources.
                </td>
              </tr>
            ) : (
              filteredSources.map((source) => {
                const info = statusInfo(source.status)
                const selected = sourceFilter === source.sourceId
                const sourceNotes = notes(source)
                return (
                  <tr key={source.sourceId} className={selected ? 'is-filter' : undefined}>
                    <td>
                      {backendDown ? (
                        <StatusBadge
                          info={BACKEND_UNREACHABLE_INFO}
                          title={`Last known: ${info.label}. ${BACKEND_UNREACHABLE_INFO.hint}`}
                        />
                      ) : (
                        <StatusBadge info={info} />
                      )}
                    </td>
                    <td>
                      <button
                        type="button"
                        className="link"
                        aria-pressed={selected}
                        title={selected ? 'Clear the prices query source filter' : `Filter prices to ${source.sourceId}`}
                        onClick={() => props.onToggleSource(source.sourceId)}
                      >
                        {source.sourceId}
                      </button>
                      {source.isMock ? <span className="badge badge-mock badge-inline">MOCK</span> : null}
                      {source.isSynthetic && !source.isMock ? <span className="badge badge-synthetic badge-inline">SYNTHETIC</span> : null}
                    </td>
                    <td title={source.description}>{source.kind.toUpperCase()}</td>
                    <td className="num">{formatInteger(source.recordCount)}</td>
                    <td className="mono">{timeWithAge(source.lastSnapshotAtUtc, serverTimeUtc)}</td>
                    <td className="mono">{timeWithAge(source.lastUpdateReceivedAtUtc, serverTimeUtc)}</td>
                    <td className="mono">{source.nextSnapshotDueAtUtc ? formatUtcTime(source.nextSnapshotDueAtUtc) : '—'}</td>
                    <td className="notes">
                      {source.csvImport ? <span className="tag">CSV CHECK {timeWithAge(source.csvImport.lastFileCheckAtUtc, serverTimeUtc)}</span> : null}
                      {source.csvImport?.lastDataImportAtUtc ? <span className="tag">DATA {timeWithAge(source.csvImport.lastDataImportAtUtc, serverTimeUtc)}</span> : null}
                      {sourceNotes.map((note) => (
                        <span key={note} className="tag">
                          {note}
                        </span>
                      ))}
                      {source.lastError ? (
                        <span className="err" title={source.lastError}>
                          ERR: {source.lastError}
                        </span>
                      ) : null}
                    </td>
                    <td>
                      {source.isMock ? (
                        <button
                          type="button"
                          className="btn btn-small"
                          disabled={outageDisabled}
                          title={`Simulate a ${OUTAGE_SECONDS}s outage of this mock source (feed down, snapshots fail)`}
                          onClick={() => props.onOutage(source.sourceId, OUTAGE_SECONDS)}
                        >
                          Outage {OUTAGE_SECONDS}s
                        </button>
                      ) : null}
                    </td>
                  </tr>
                )
              })
            )}
          </tbody>
        </table>
      </div>
      </div> : null}
    </section>
  )
}

import type { SourceDto } from '../api/types'
import type { ActionKind } from '../hooks/useAdminActions'
import { formatAgeBetween, formatInteger, formatUtcTime } from '../lib/format'
import { BACKEND_UNREACHABLE_INFO, statusInfo } from '../lib/status'
import { StatusBadge } from './StatusBadge'

const OUTAGE_SECONDS = 20

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
  const outageDisabled = backendDown || props.adminEnabled !== true || props.busy !== null

  return (
    <section className="panel sources" aria-labelledby="sources-title">
      <h2 id="sources-title" className="panel-title">
        EQUITY PRICE SOURCES
      </h2>
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
            ) : sources.length === 0 ? (
              <tr>
                <td colSpan={9} className="empty">
                  The backend reports no sources.
                </td>
              </tr>
            ) : (
              sources.map((source) => {
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
                        title={selected ? 'Clear the source filter' : `Filter prices to ${source.sourceId}`}
                        onClick={() => props.onToggleSource(source.sourceId)}
                      >
                        {source.sourceId}
                      </button>
                      {source.isMock ? <span className="badge badge-mock badge-inline">MOCK</span> : null}
                    </td>
                    <td title={source.description}>{source.kind.toUpperCase()}</td>
                    <td className="num">{formatInteger(source.recordCount)}</td>
                    <td className="mono">{timeWithAge(source.lastSnapshotAtUtc, serverTimeUtc)}</td>
                    <td className="mono">{timeWithAge(source.lastUpdateReceivedAtUtc, serverTimeUtc)}</td>
                    <td className="mono">{source.nextSnapshotDueAtUtc ? formatUtcTime(source.nextSnapshotDueAtUtc) : '—'}</td>
                    <td className="notes">
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
    </section>
  )
}

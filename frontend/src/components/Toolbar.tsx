import type { ActionKind, ActionResult } from '../hooks/useAdminActions'
import type { SourceDto } from '../api/types'

interface ToolbarProps {
  sources: readonly SourceDto[]
  sourceFilter: string | null
  onSourceFilter: (sourceId: string | null) => void
  instrumentInput: string
  onInstrumentInput: (value: string) => void
  /** null＝未知（未收到 health）。 */
  adminEnabled: boolean | null
  backendDown: boolean
  busy: ActionKind | null
  actionResult: ActionResult | null
  onRefreshSnapshot: () => void
  onSaveCheckpoint: () => void
}

function disabledReason(adminEnabled: boolean | null, backendDown: boolean, busy: ActionKind | null): string | undefined {
  if (backendDown) {
    return 'Backend is unreachable.'
  }

  if (adminEnabled === null) {
    return 'Waiting for the backend.'
  }

  if (!adminEnabled) {
    return 'Admin endpoints are disabled on this backend (DataHub:Api:AdminEnabled=false).'
  }

  return busy ? 'Another action is running.' : undefined
}

export function Toolbar(props: ToolbarProps) {
  const { sources, sourceFilter, adminEnabled, backendDown, busy, actionResult } = props
  const reason = disabledReason(adminEnabled, backendDown, busy)
  const snapshotLabel = sourceFilter ? `Refresh snapshot: ${sourceFilter}` : 'Refresh snapshot: all'

  return (
    <div className="tb" role="toolbar" aria-label="Filters and actions">
      <div className="field">
        <label htmlFor="source-filter">SOURCE</label>
        <select
          id="source-filter"
          className="ctl"
          value={sourceFilter ?? ''}
          onChange={(event) => props.onSourceFilter(event.target.value || null)}
        >
          <option value="">ALL SOURCES</option>
          {sources.map((source) => (
            <option key={source.sourceId} value={source.sourceId}>
              {source.sourceId}
            </option>
          ))}
          {sourceFilter && !sources.some((s) => s.sourceId === sourceFilter) ? (
            <option value={sourceFilter}>{sourceFilter}</option>
          ) : null}
        </select>
      </div>

      <div className="field">
        <label htmlFor="instrument-search">INSTRUMENT</label>
        <input
          id="instrument-search"
          className="ctl ctl-search"
          type="search"
          inputMode="search"
          autoComplete="off"
          spellCheck={false}
          maxLength={64}
          placeholder="contains…"
          value={props.instrumentInput}
          onChange={(event) => props.onInstrumentInput(event.target.value)}
        />
      </div>

      <div className="tb-actions">
        <button type="button" className="btn" disabled={reason !== undefined} title={reason ?? snapshotLabel} onClick={props.onRefreshSnapshot}>
          {busy === 'snapshot' ? 'Refreshing…' : snapshotLabel}
        </button>
        <button type="button" className="btn" disabled={reason !== undefined} title={reason ?? 'Write a checkpoint file now'} onClick={props.onSaveCheckpoint}>
          {busy === 'checkpoint' ? 'Saving…' : 'Save checkpoint'}
        </button>
      </div>

      <output className={`action-msg${actionResult ? (actionResult.ok ? ' ok' : ' err') : ''}`} title={actionResult?.text}>
        {actionResult ? (
          <>
            <strong>{actionResult.ok ? 'OK' : 'ERROR'}</strong> {actionResult.text}
          </>
        ) : null}
      </output>
    </div>
  )
}

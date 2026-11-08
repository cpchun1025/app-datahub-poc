import { useEffect, useRef, useState } from 'react'
import { api, describeApiError, type ExportScope, type PriceQuery } from '../api/client'
import type { SourceStatus } from '../api/types'
import type { GridFilters, TechnicalColumns } from '../lib/grid'

const statuses: readonly SourceStatus[] = ['Initializing', 'Live', 'Recovering', 'Stale', 'Disconnected']
const columnLabels: Readonly<Record<keyof TechnicalColumns, string>> = {
  sourceTime: 'Source time', receivedTime: 'Received time', revision: 'Revision',
}

interface GridControlsProps {
  query: PriceQuery
  filters: GridFilters
  onFilters: (filters: GridFilters) => void
  onReset: () => void
  paused: boolean
  onPaused: (paused: boolean) => void
  columns: TechnicalColumns
  onColumns: (columns: TechnicalColumns) => void
  exportDisabled: boolean
}

export function GridControls(props: GridControlsProps) {
  const [busy, setBusy] = useState<ExportScope | null>(null)
  const [error, setError] = useState<string | null>(null)
  const active = useRef<AbortController | null>(null)
  useEffect(() => () => active.current?.abort(), [])

  async function download(scope: ExportScope) {
    if (active.current !== null) return
    const controller = new AbortController()
    active.current = controller
    setBusy(scope)
    setError(null)
    try {
      const blob = await api.exportPrices(props.query, scope, controller.signal)
      if (controller.signal.aborted) return
      const url = URL.createObjectURL(blob)
      try {
        const link = document.createElement('a')
        link.href = url
        link.download = `prices-${scope}.csv`
        link.click()
      } finally {
        URL.revokeObjectURL(url)
      }
    } catch (failure) {
      if (!controller.signal.aborted) setError(describeApiError(failure))
    } finally {
      active.current = null
      if (!controller.signal.aborted) setBusy(null)
    }
  }

  return (
    <div className="grid-controls">
      <fieldset className="tb">
        <legend className="sr-only">Price grid filters and display</legend>
        <div className="field">
          <label htmlFor="currency-filter">CCY</label>
          <input id="currency-filter" className="ctl ctl-currency" maxLength={3} placeholder="ALL"
            value={props.filters.currency} autoComplete="off" spellCheck={false}
            onChange={(e) => props.onFilters({ ...props.filters, currency: e.target.value.toUpperCase() })} />
        </div>
        <div className="field">
          <label htmlFor="status-filter">STATUS</label>
          <select id="status-filter" className="ctl" value={props.filters.sourceStatus}
            onChange={(e) => props.onFilters({ ...props.filters, sourceStatus: statuses.find(s => s === e.target.value) ?? '' })}>
            <option value="">ALL STATUSES</option>
            {statuses.map(status => <option key={status} value={status}>{status.toUpperCase()}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="min-price">MIN PRICE</label>
          <input id="min-price" className="ctl ctl-price" inputMode="decimal" placeholder="no minimum"
            value={props.filters.minPrice} onChange={(e) => props.onFilters({ ...props.filters, minPrice: e.target.value })} />
        </div>
        <div className="field">
          <label htmlFor="max-price">MAX PRICE</label>
          <input id="max-price" className="ctl ctl-price" inputMode="decimal" placeholder="no maximum"
            value={props.filters.maxPrice} onChange={(e) => props.onFilters({ ...props.filters, maxPrice: e.target.value })} />
        </div>
        <button className="btn" type="button" onClick={props.onReset}>Reset filters/sort</button>
        <details className="columns-menu">
          <summary className="btn">Columns</summary>
          <div className="columns-options">
            {(['sourceTime', 'receivedTime', 'revision'] as const).map(key => (
              <label key={key}>
                <input type="checkbox" checked={props.columns[key]}
                  onChange={(e) => props.onColumns({ ...props.columns, [key]: e.target.checked })} />
                {columnLabels[key]}
              </label>
            ))}
          </div>
        </details>
        <button className="btn" type="button" aria-pressed={props.paused} onClick={() => props.onPaused(!props.paused)}>
          {props.paused ? 'Resume' : 'Pause updates'}
        </button>
        {props.paused ? <output className="display-paused">DISPLAY PAUSED</output> : null}
      </fieldset>
      <fieldset className="tb csv-controls">
        <legend className="sr-only">CSV export</legend>
        <button className="btn" type="button" disabled={props.exportDisabled || busy !== null} onClick={() => void download('page')}>
          {busy === 'page' ? 'Exporting…' : 'Export current page'}
        </button>
        <button className="btn" type="button" disabled={props.exportDisabled || busy !== null} onClick={() => void download('all')}>
          {busy === 'all' ? 'Exporting…' : 'Export all matching'}
        </button>
        <span className="dim">CSV queries request-time data, not this screen. Excel: import INSTRUMENT as Text.</span>
        {error ? <span className="err export-error" role="alert">{error}</span> : null}
      </fieldset>
    </div>
  )
}

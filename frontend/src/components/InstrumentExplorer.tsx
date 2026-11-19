import { useEffect, useState } from 'react'
import type { InstrumentDetailResponse, InstrumentMembershipResponse, PriceDto } from '../api/types'
import { api, describeApiError } from '../api/client'
import { formatPrice, formatUtcDateTimeMs, formatUtcTime } from '../lib/format'
import { statusInfo } from '../lib/status'
import { PriceDetail } from './PriceDetail'

const PAGE_SIZE = 10

interface ExplorerState {
  key: string | null
  instrument: InstrumentDetailResponse | null
  memberships: InstrumentMembershipResponse | null
  error: string | null
}

interface InstrumentExplorerProps {
  row: PriceDto | null
  decimals: number | undefined
}

export function InstrumentExplorer({ row, decimals }: InstrumentExplorerProps) {
  const instrumentId = row?.instrumentId ?? null
  const [instrumentPageState, setInstrumentPageState] = useState({ instrumentId: null as string | null, page: 1 })
  const [membershipPageState, setMembershipPageState] = useState({ instrumentId: null as string | null, page: 1 })
  const instrumentPage = instrumentPageState.instrumentId === instrumentId ? instrumentPageState.page : 1
  const membershipPage = membershipPageState.instrumentId === instrumentId ? membershipPageState.page : 1
  const requestKey = instrumentId ? `${instrumentId}:${instrumentPage}:${membershipPage}` : null
  const [state, setState] = useState<ExplorerState>({
    key: null, instrument: null, memberships: null, error: null,
  })

  useEffect(() => {
    if (!instrumentId || requestKey === null) return

    const controller = new AbortController()
    void Promise.all([
      api.instrument(instrumentId, instrumentPage, PAGE_SIZE, controller.signal),
      api.memberships(instrumentId, membershipPage, PAGE_SIZE, controller.signal),
    ]).then(([instrument, memberships]) => {
      setState({ key: requestKey, instrument, memberships, error: null })
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) {
        setState({ key: requestKey, instrument: null, memberships: null, error: describeApiError(error) })
      }
    })

    return () => controller.abort()
  }, [instrumentId, instrumentPage, membershipPage, requestKey])

  const currentState = state.key === requestKey ? state : null
  const loading = requestKey !== null && currentState === null
  const setInstrumentPage = (page: number) => {
    if (instrumentId) setInstrumentPageState({ instrumentId, page })
  }
  const setMembershipPage = (page: number) => {
    if (instrumentId) setMembershipPageState({ instrumentId, page })
  }

  return (
    <section className="instrument-explorer" aria-labelledby="instrument-explorer-title" aria-busy={loading}>
      <h3 id="instrument-explorer-title" className="panel-title">
        INSTRUMENT EXPLORER
        {instrumentId ? <span className="crumb"> · {instrumentId}</span> : null}
      </h3>
      {!instrumentId ? <p className="empty">Select a price row to inspect source observations and memberships.</p> : null}
      {loading ? <output className="dim">Loading instrument detail…</output> : null}
      {currentState?.error ? <p role="alert" className="err">Instrument detail unavailable: {currentState.error}</p> : null}
      {currentState?.instrument ? <div className="instrument-explorer-grid">
        <section aria-labelledby="observations-title">
          <h4 id="observations-title">SPOT OBSERVATIONS · {currentState.instrument.totalCount}</h4>
          <p className="dataset-as-of">Response as of {formatUtcTime(currentState.instrument.serverTimeUtc)} UTC · source values may have different as-of times</p>
          {currentState.instrument.observations.length === 0 ? <p className="empty">No spot observations are currently available.</p> : (
            <div className="related-list">
              {currentState.instrument.observations.map((observation) => (
                <article className="related-row" key={observation.sourceId}>
                  <strong>{observation.sourceId}</strong>
                  {observation.isSynthetic ? <span className="badge badge-synthetic">SYNTHETIC</span> : null}
                  <span>{observation.providerSymbol ?? observation.instrumentId}</span>
                  <span className="num">{formatPrice(observation.price, decimals)} {observation.currency}</span>
                  <span>{observation.priceKind ?? 'UNKNOWN KIND'}</span>
                  <span>{statusInfo(observation.sourceStatus).label}</span>
                  <span className="mono">OBS {formatUtcDateTimeMs(observation.sourceUpdatedAtUtc)} UTC</span>
                  <span className="mono">RECV {formatUtcDateTimeMs(observation.receivedAtUtc)} UTC</span>
                </article>
              ))}
            </div>
          )}
          <PageControls page={instrumentPage} pages={currentState.instrument.totalPages} onChange={setInstrumentPage} />
        </section>
        <section aria-labelledby="memberships-title">
          <h4 id="memberships-title">SYNTHETIC INDEX MEMBERSHIPS · {currentState.memberships?.totalCount ?? 0}</h4>
          {currentState.memberships ? <>
            <p className="dataset-as-of">
              {currentState.memberships.sourceId} · {currentState.memberships.sourceStatus} · validated{' '}
              {currentState.memberships.lastSuccessfulValidationAtUtc
                ? `${formatUtcTime(currentState.memberships.lastSuccessfulValidationAtUtc)} UTC`
                : '—'}
            </p>
            {currentState.memberships.lastError ? <p role="alert" className="err">{currentState.memberships.lastError}</p> : null}
            {currentState.memberships.items.length === 0 ? <p className="empty">No synthetic membership data is available for this instrument.</p> : (
              <div className="related-list">
                {currentState.memberships.items.map((membership) => (
                  <article className="related-row membership-row" key={`${membership.sourceId}/${membership.indexId}/${membership.effectiveDate}`}>
                    <strong>{membership.indexId}</strong>
                    <span className="badge badge-synthetic">SYNTHETIC</span>
                    <span>Weight {membership.weight.toLocaleString(undefined, { maximumFractionDigits: 8 })}</span>
                    <span>Effective {membership.effectiveDate}</span>
                    <span>{membership.sourceId}</span>
                  </article>
                ))}
              </div>
            )}
            <PageControls page={membershipPage} pages={currentState.memberships.totalPages} onChange={setMembershipPage} />
          </> : null}
        </section>
      </div> : null}
      <details className="diagnostics">
        <summary>DIAGNOSTICS · selected source record</summary>
        <PriceDetail row={row} decimals={decimals} />
      </details>
    </section>
  )
}

function PageControls({ page, pages, onChange }: { page: number; pages: number; onChange: (page: number) => void }) {
  if (pages <= 1) return null
  return (
    <nav className="related-pagination" aria-label="Instrument detail pagination">
      <button className="btn btn-small" disabled={page <= 1} onClick={() => onChange(page - 1)}>Previous</button>
      <span>Page {page} of {pages}</span>
      <button className="btn btn-small" disabled={page >= pages} onClick={() => onChange(page + 1)}>Next</button>
    </nav>
  )
}

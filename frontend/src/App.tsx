import { useCallback, useMemo, useState } from 'react'
import { priceQueryKey, type PriceQuery } from './api/client'
import { Banners } from './components/Banners'
import { Header } from './components/Header'
import { Pagination } from './components/Pagination'
import { PriceDetail } from './components/PriceDetail'
import { PricesTable } from './components/PricesTable'
import { SourcePanel } from './components/SourcePanel'
import { StatusBar } from './components/StatusBar'
import { Toolbar } from './components/Toolbar'
import { GridControls } from './components/GridControls'
import { config } from './config'
import { useAdminActions } from './hooks/useAdminActions'
import { useDataHub } from './hooks/useDataHub'
import { useDebouncedValue } from './hooks/useDebouncedValue'
import { firstApiError } from './lib/dataHubState'
import { emptyPricesMessage } from './lib/emptyState'
import { rowKey } from './lib/flash'
import { defaultColumns, defaultFilters, defaultSort, toggleSort } from './lib/grid'

export default function App() {
  const [sourceFilter, setSourceFilter] = useState<string | null>(null)
  const [instrumentInput, setInstrumentInput] = useState('')
  const [pageSize, setPageSize] = useState<number>(config.defaultPageSize)
  const [filters, setFilters] = useState(defaultFilters)
  const [sort, setSort] = useState(defaultSort)
  const [columns, setColumns] = useState(defaultColumns)
  const [paused, setPaused] = useState(false)
  const instrument = useDebouncedValue(instrumentInput.trim(), config.searchDebounceMs)
  const currency = useDebouncedValue(filters.currency.trim(), config.searchDebounceMs)
  const minPrice = useDebouncedValue(filters.minPrice.trim(), config.searchDebounceMs)
  const maxPrice = useDebouncedValue(filters.maxPrice.trim(), config.searchDebounceMs)
  const sourceStatus = filters.sourceStatus

  // 記住新 query 條件，避免來回切換 filter／sort／每頁筆數後恢復舊頁碼。
  const filterKey = priceQueryKey({
    sourceId: sourceFilter, instrument, pageSize, page: 1,
    currency, sourceStatus, minPrice, maxPrice, sortBy: sort.field, sortDirection: sort.direction,
  })
  const [pageState, setPageState] = useState({ filterKey, page: 1 })
  const page = pageState.filterKey === filterKey ? pageState.page : 1
  if (pageState.filterKey !== filterKey) {
    setPageState({ filterKey, page: 1 })
  }
  const setPage = useCallback((next: number) => setPageState({ filterKey, page: next }), [filterKey])

  const query = useMemo<PriceQuery>(
    () => ({ sourceId: sourceFilter, instrument, page, pageSize,
      currency, sourceStatus, minPrice, maxPrice, sortBy: sort.field, sortDirection: sort.direction }),
    [sourceFilter, instrument, page, pageSize, currency, sourceStatus, minPrice, maxPrice, sort],
  )
  const queryKey = priceQueryKey(query)

  // 選取嘅 row 只屬於產生佢嘅 query（換 filter／頁碼就清走），polling 期間保持選取。
  const [selection, setSelection] = useState<{ queryKey: string; key: string | null }>({ queryKey, key: null })
  const selectedKey = selection.queryKey === queryKey ? selection.key : null
  if (selection.queryKey !== queryKey) {
    setSelection({ queryKey, key: null })
  }
  const select = useCallback((key: string | null) => setSelection({ queryKey, key }), [queryKey])

  const { state, refreshNow } = useDataHub(query, config.pollIntervalMs, paused)
  const actions = useAdminActions(refreshNow)

  const { prices, health } = state
  const backendDown = state.connection === 'disconnected'
  const sourceList = state.sources?.sources ?? null
  const items = prices?.items ?? []
  const apiError = firstApiError(state)

  // 換咗 query 但新回應未到：保留舊表格（變暗）而唔係清空，避免畫面跳動。
  const viewIsStale = prices !== null && state.pricesQueryKey !== queryKey
  const totalPages = prices?.totalPages ?? null
  const pageOutOfRange = !viewIsStale && prices !== null && prices.items.length === 0 && prices.totalCount > 0

  // 資料集縮細令目前頁碼超出範圍（例如 snapshot 移除咗 instrument）：喺 render 期間直接調返去最後一頁。
  // 調整後 query 已經改變（viewIsStale），所以唔會重複觸發。
  if (!viewIsStale && totalPages !== null && totalPages > 0 && page > totalPages) {
    setPageState({ filterKey, page: totalPages })
  }

  const sourceDecimals = new Map((sourceList ?? []).map((source) => [source.sourceId, source.priceDecimals]))
  const selectedRow =
    selectedKey === null ? null : (items.find((row) => rowKey(row.sourceId, row.instrumentId) === selectedKey) ?? null)

  const filtered = sourceFilter !== null || instrument !== '' || currency !== '' || sourceStatus !== '' || minPrice !== '' || maxPrice !== ''
  const resetGrid = () => {
    setSourceFilter(null)
    setInstrumentInput('')
    setFilters(defaultFilters)
    setSort(defaultSort)
    setPage(1)
  }
  const range =
    prices && prices.items.length > 0
      ? {
          from: (prices.page - 1) * prices.pageSize + 1,
          to: (prices.page - 1) * prices.pageSize + prices.items.length,
        }
      : null

  const summary = health?.sources
  const refreshSnapshot = () => {
    const ids = sourceFilter ? [sourceFilter] : (sourceList ?? []).map((source) => source.sourceId)
    void actions.refreshSnapshots(ids)
  }

  const emptyMessage = emptyPricesMessage({
    connection: state.connection,
    hasPrices: prices !== null,
    filtered,
    pricesFailed: state.errors.prices !== null && !backendDown,
    pageOutOfRange,
  })

  return (
    <div className="app">
      <Header
        connection={state.connection}
        hasApiError={apiError !== null}
        mockMode={health?.mockMode ?? null}
        lastResponseMs={state.lastRefreshMs}
        disconnectedSinceMs={state.disconnectedSinceMs}
        live={backendDown || !summary ? null : summary.live}
        total={backendDown || !summary ? null : summary.total}
      />
      <Banners
        connection={state.connection}
        disconnectedSinceMs={state.disconnectedSinceMs}
        lastRefreshMs={state.lastRefreshMs}
        pollIntervalMs={config.pollIntervalMs}
        apiError={apiError}
        sources={sourceList}
      />
      <Toolbar
        sources={sourceList ?? []}
        sourceFilter={sourceFilter}
        onSourceFilter={setSourceFilter}
        instrumentInput={instrumentInput}
        onInstrumentInput={setInstrumentInput}
        adminEnabled={health?.adminEnabled ?? null}
        backendDown={backendDown}
        busy={actions.busy}
        actionResult={actions.result}
        onRefreshSnapshot={refreshSnapshot}
        onSaveCheckpoint={() => void actions.saveCheckpoint()}
      />
      <SourcePanel
        sources={sourceList}
        serverTimeUtc={state.sources?.serverTimeUtc ?? null}
        backendDown={backendDown}
        sourceFilter={sourceFilter}
        onToggleSource={(sourceId) => setSourceFilter((current) => (current === sourceId ? null : sourceId))}
        adminEnabled={health?.adminEnabled ?? null}
        busy={actions.busy}
        onOutage={(sourceId, seconds) => void actions.simulateOutage(sourceId, seconds)}
      />
      <main className="panel prices" aria-labelledby="prices-title">
        <h2 id="prices-title" className="panel-title">
          PRICES
          {sourceFilter ? <span className="crumb"> · {sourceFilter}</span> : null}
          {instrument ? <span className="crumb"> · “{instrument}”</span> : null}
          <span className="panel-note">net change between UI polls, not every tick</span>
        </h2>
        <GridControls query={query} filters={filters} onFilters={setFilters} onReset={resetGrid}
          paused={paused} onPaused={setPaused} columns={columns} onColumns={setColumns}
          exportDisabled={backendDown || health === null} />
        <div
          className={`prices-scroll${viewIsStale || backendDown ? ' is-dim' : ''}`}
          aria-busy={viewIsStale || backendDown}
        >
          <PricesTable
            items={items}
            sourceDecimals={sourceDecimals}
            flashes={state.flashes}
            backendDown={backendDown}
            selectedKey={selectedKey}
            onSelect={select}
            emptyMessage={emptyMessage}
            sort={sort}
            onSort={(field) => setSort(current => toggleSort(current, field))}
            columns={columns}
          />
        </div>
        <PriceDetail row={selectedRow} decimals={selectedRow ? sourceDecimals.get(selectedRow.sourceId) : undefined} />
        <Pagination
          page={page}
          pageSize={pageSize}
          pageSizes={config.pageSizes}
          totalCount={prices?.totalCount ?? null}
          totalPages={totalPages}
          range={range}
          loading={viewIsStale && !backendDown}
          onPage={setPage}
          onPageSize={setPageSize}
        />
      </main>
      <StatusBar
        lastRefreshMs={state.lastRefreshMs}
        pollIntervalMs={config.pollIntervalMs}
        health={health}
        backendDown={backendDown}
        totalCount={prices?.totalCount ?? null}
        errors={state.errors}
        sourceErrors={(sourceList ?? []).filter((source) => source.lastError).map((source) => source.sourceId)}
        paused={paused}
      />
    </div>
  )
}

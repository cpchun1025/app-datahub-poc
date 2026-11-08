import { formatInteger } from '../lib/format'

interface PaginationProps {
  /** 使用者請求緊嘅頁碼（1-based）。 */
  page: number
  pageSize: number
  pageSizes: readonly number[]
  /** 最近一次回應嘅資料；未有回應時為 null。 */
  totalCount: number | null
  totalPages: number | null
  /** 畫面而家顯示嘅 row 範圍（由 server 回應推算，唔係請求緊嘅頁碼）。 */
  range: { from: number; to: number } | null
  /** 畫面仍然係上一個 query 嘅資料，等緊新回應。 */
  loading: boolean
  onPage: (page: number) => void
  onPageSize: (pageSize: number) => void
}

export function Pagination({
  page,
  pageSize,
  pageSizes,
  totalCount,
  totalPages,
  range,
  loading,
  onPage,
  onPageSize,
}: PaginationProps) {
  const lastPage = Math.max(1, totalPages ?? 1)
  const rangeText =
    totalCount === null
      ? '—'
      : range === null
        ? `0 OF ${formatInteger(totalCount)}`
        : `${formatInteger(range.from)}–${formatInteger(range.to)} OF ${formatInteger(totalCount)}`

  return (
    <nav className="pager" aria-label="Pagination">
      <span className="pager-range">
        <span className="k">ROWS</span> {rangeText}
      </span>
      {loading ? <span className="pager-loading">LOADING…</span> : null}
      <span className="hdr-spacer" />
      <button type="button" className="btn btn-small" aria-label="First page" title="First page" disabled={page <= 1} onClick={() => onPage(1)}>
        «
      </button>
      <button type="button" className="btn btn-small" aria-label="Previous page" title="Previous page" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        ‹
      </button>
      <span className="pager-page">
        <span className="k">PAGE</span> {page} / {lastPage}
      </span>
      <button type="button" className="btn btn-small" aria-label="Next page" title="Next page" disabled={page >= lastPage} onClick={() => onPage(page + 1)}>
        ›
      </button>
      <button type="button" className="btn btn-small" aria-label="Last page" title="Last page" disabled={page >= lastPage} onClick={() => onPage(lastPage)}>
        »
      </button>
      <label className="pager-size">
        <span className="k">PER PAGE</span>
        <select className="ctl" value={pageSize} onChange={(event) => onPageSize(Number(event.target.value))}>
          {pageSizes.map((size) => (
            <option key={size} value={size}>
              {size}
            </option>
          ))}
        </select>
      </label>
    </nav>
  )
}

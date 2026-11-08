import { useRef, type KeyboardEvent, type ReactNode } from 'react'
import type { PriceDto } from '../api/types'
import type { PriceSortField } from '../api/client'
import type { GridSort, TechnicalColumns } from '../lib/grid'
import { rowKey, type Flash } from '../lib/flash'
import { formatPrice, formatUtcTimeMs } from '../lib/format'
import { BACKEND_UNREACHABLE_INFO, statusInfo } from '../lib/status'
import { StatusBadge } from './StatusBadge'

const PAGE_STEP = 10

interface PricesTableProps {
  items: readonly PriceDto[]
  /** sourceId → priceDecimals；未知來源顯示原值。 */
  sourceDecimals: ReadonlyMap<string, number>
  flashes: ReadonlyMap<string, Flash>
  backendDown: boolean
  selectedKey: string | null
  onSelect: (key: string | null) => void
  /** items 為空時顯示嘅說明（loading／empty／error）。 */
  emptyMessage: ReactNode
  sort: GridSort
  onSort: (field: PriceSortField) => void
  columns: TechnicalColumns
}

export function PricesTable({
  items,
  sourceDecimals,
  flashes,
  backendDown,
  selectedKey,
  onSelect,
  emptyMessage,
  sort,
  onSort,
  columns,
}: PricesTableProps) {
  const bodyRef = useRef<HTMLTableSectionElement>(null)
  const keys = items.map((row) => rowKey(row.sourceId, row.instrumentId))
  // roving tabindex：整個 table 只有一個 tab stop（已選取嘅 row，否則第一行）。
  const tabStopKey = selectedKey !== null && keys.includes(selectedKey) ? selectedKey : (keys[0] ?? null)
  const columnCount = 7 + Number(columns.sourceTime) + Number(columns.receivedTime) + Number(columns.revision)
  const sortHeader = (field: PriceSortField, label: string, className?: string) => (
    <th scope="col" className={className}
      aria-sort={sort.field === field ? sort.direction === 'asc' ? 'ascending' : 'descending' : 'none'}>
      <button type="button" className="sort-header" onClick={() => onSort(field)}>
        {label} <span aria-hidden="true">{sort.field === field ? sort.direction === 'asc' ? '▲' : '▼' : '↕'}</span>
      </button>
    </th>
  )

  const moveTo = (index: number) => {
    const key = keys[index]
    if (key === undefined) {
      return
    }

    bodyRef.current?.querySelector<HTMLTableRowElement>(`tr[data-index="${index}"]`)?.focus()
    onSelect(key)
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLTableRowElement>, index: number) => {
    const last = keys.length - 1
    switch (event.key) {
      case 'ArrowDown':
        moveTo(Math.min(index + 1, last))
        break
      case 'ArrowUp':
        moveTo(Math.max(index - 1, 0))
        break
      case 'PageDown':
        moveTo(Math.min(index + PAGE_STEP, last))
        break
      case 'PageUp':
        moveTo(Math.max(index - PAGE_STEP, 0))
        break
      case 'Home':
        moveTo(0)
        break
      case 'End':
        moveTo(last)
        break
      case 'Enter':
      case ' ': {
        const key = keys[index]
        if (key !== undefined) {
          onSelect(key)
        }
        break
      }
      case 'Escape':
        onSelect(null)
        break
      default:
        return
    }

    event.preventDefault()
  }

  return (
    <table className="grid grid-prices">
      <colgroup>
        <col className="col-source" />
        <col className="col-instrument" />
        <col className="col-price" />
        <col className="col-chg" />
        <col className="col-ccy" />
        {columns.sourceTime ? <col className="col-time" /> : null}
        {columns.receivedTime ? <col className="col-time" /> : null}
        {columns.revision ? <col className="col-rev" /> : null}
        <col className="col-status" />
        <col />
      </colgroup>
      <thead>
        <tr>
          {sortHeader('sourceId', 'SOURCE')}
          {sortHeader('instrumentId', 'INSTRUMENT')}
          {sortHeader('price', 'PRICE', 'num')}
          <th scope="col" className="chg">
            <span className="sr-only">Change since the previous screen</span>
            <span aria-hidden="true">CHG</span>
          </th>
          {sortHeader('currency', 'CCY')}
          {columns.sourceTime ? sortHeader('sourceUpdatedAtUtc', 'SOURCE TIME') : null}
          {columns.receivedTime ? sortHeader('receivedAtUtc', 'RECEIVED') : null}
          {columns.revision ? <th scope="col" className="num">REV</th> : null}
          <th scope="col">STATUS</th>
          <th scope="col" aria-hidden="true" />
        </tr>
      </thead>
      <tbody ref={bodyRef}>
        {items.length === 0 ? (
          <tr>
            <td colSpan={columnCount} className="empty">
              {emptyMessage}
            </td>
          </tr>
        ) : (
          items.map((row, index) => {
            const key = keys[index] ?? ''
            const flash = flashes.get(key)
            const selected = key === selectedKey
            const info = statusInfo(row.sourceStatus)
            const price = formatPrice(row.price, sourceDecimals.get(row.sourceId))

            return (
              <tr
                key={key}
                data-index={index}
                tabIndex={key === tabStopKey ? 0 : -1}
                aria-selected={selected}
                className={selected ? 'is-selected' : undefined}
                onClick={() => onSelect(key)}
                onKeyDown={(event) => handleKeyDown(event, index)}
              >
                <td>{row.sourceId}</td>
                <td className="instrument" title={row.instrumentId}>
                  {row.instrumentId}
                </td>
                <td className="num price">
                  {/* key 隨 flash.seq 改變，令連續同方向變動都會重新播放動畫。 */}
                  <span key={flash ? flash.seq : 'idle'} className={flash ? `flash flash-${flash.direction}` : undefined}>
                    {price}
                  </span>
                </td>
                <td className={flash ? `chg chg-${flash.direction}` : 'chg'}>
                  {flash ? (
                    <>
                      <span aria-hidden="true">{flash.direction === 'up' ? '▲' : '▼'}</span>
                      <span className="sr-only">{flash.direction === 'up' ? 'Price up' : 'Price down'}</span>
                    </>
                  ) : null}
                </td>
                <td>{row.currency}</td>
                {columns.sourceTime ? <td className="mono">{formatUtcTimeMs(row.sourceUpdatedAtUtc)}</td> : null}
                {columns.receivedTime ? <td className="mono">{formatUtcTimeMs(row.receivedAtUtc)}</td> : null}
                {columns.revision ? <td className="num">{row.sourceRevision}</td> : null}
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
                <td aria-hidden="true" />
              </tr>
            )
          })
        )}
      </tbody>
    </table>
  )
}

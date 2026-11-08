import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PricesTable } from './PricesTable'
import { defaultColumns, defaultSort, toggleSort } from '../lib/grid'
import type { PriceDto } from '../api/types'

const row: PriceDto = {
  sourceId: 'alpha', instrumentId: '0000123', price: 10, currency: 'USD',
  sourceUpdatedAtUtc: '2026-01-01T00:00:00Z', receivedAtUtc: '2026-01-01T00:00:00Z',
  sourceRevision: 1, sourceStatus: 'Recovering',
}
const props = {
  items: [row], sourceDecimals: new Map(), flashes: new Map(),
  backendDown: false, selectedKey: null, onSelect: () => {}, emptyMessage: 'Empty',
  sort: defaultSort, onSort: () => {}, columns: defaultColumns,
}

describe('grid ordering and columns', () => {
  it('toggles one column and starts a newly selected column ascending', () => {
    expect(toggleSort(defaultSort, 'sourceId')).toEqual({ field: 'sourceId', direction: 'desc' })
    expect(toggleSort({ field: 'price', direction: 'desc' }, 'price')).toEqual({ field: 'price', direction: 'asc' })
    expect(toggleSort(defaultSort, 'price')).toEqual({ field: 'price', direction: 'asc' })
  })

  it('exposes the sorted header and preserves source status and row keyboard access', () => {
    const html = renderToStaticMarkup(<PricesTable {...props} sort={{ field: 'price', direction: 'desc' }} />)
    expect(html).toContain('class="num" aria-sort="descending"')
    expect(html).toContain('RECOVERING')
    expect(html).toContain('tabindex="0"')
    expect(html).toContain('0000123')
    expect(html.match(/class="sort-header"/g)).toHaveLength(6)
  })

  it('can hide all technical columns and keeps the empty-row span correct', () => {
    const html = renderToStaticMarkup(<PricesTable {...props} items={[]} columns={{ sourceTime: false, receivedTime: false, revision: false }} />)
    expect(html).toContain('colSpan="7"')
    expect(html).not.toContain('SOURCE TIME')
    expect(html).not.toContain('RECEIVED')
    expect(html).not.toContain('>REV<')
  })

  it('does not reorder the server results in the browser, and overlays backend disconnection', () => {
    const html = renderToStaticMarkup(<PricesTable {...props} items={[{ ...row, instrumentId: 'Z', price: 2 }, row]} backendDown />)
    expect(html.indexOf('title="Z"')).toBeLessThan(html.indexOf('title="0000123"'))
    expect(html).toContain('Last known: RECOVERING.')
    expect(html).toContain('DISCONNECTED')
  })
})

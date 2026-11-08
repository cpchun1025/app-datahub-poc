import type { PriceSortField, SortDirection } from '../api/client'
import type { SourceStatus } from '../api/types'

export interface GridFilters {
  currency: string
  sourceStatus: SourceStatus | ''
  minPrice: string
  maxPrice: string
}

export interface GridSort { field: PriceSortField; direction: SortDirection }
export interface TechnicalColumns { sourceTime: boolean; receivedTime: boolean; revision: boolean }

export const defaultFilters: GridFilters = { currency: '', sourceStatus: '', minPrice: '', maxPrice: '' }
export const defaultSort: GridSort = { field: 'sourceId', direction: 'asc' }
export const defaultColumns: TechnicalColumns = { sourceTime: true, receivedTime: true, revision: true }

export function toggleSort(current: GridSort, field: PriceSortField): GridSort {
  return { field, direction: current.field === field && current.direction === 'asc' ? 'desc' : 'asc' }
}

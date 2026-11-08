import type { ConnectionState } from './dataHubState'

export interface EmptyPricesInput {
  connection: ConnectionState
  /** 是否已經收過任何一次 prices 回應。 */
  hasPrices: boolean
  /** 有冇 source filter 或 instrument 搜尋。 */
  filtered: boolean
  /** 最近一次 prices request 失敗（backend 有回應）。 */
  pricesFailed: boolean
  /** 回應係空嘅，但 totalCount > 0（頁碼超出範圍，會自動跳返最後一頁）。 */
  pageOutOfRange: boolean
}

/** prices table 冇 row 時顯示嘅說明；每種情況都要講清楚，避免空白表格令人誤會。 */
export function emptyPricesMessage(input: EmptyPricesInput): string {
  if (!input.hasPrices) {
    if (input.connection === 'disconnected') {
      return 'Backend unreachable — no data has been received yet.'
    }

    if (input.pricesFailed) {
      return 'Prices could not be loaded — see the error above.'
    }

    return 'Loading prices…'
  }

  if (input.pageOutOfRange) {
    return 'This page no longer exists — moving to the last page…'
  }

  if (input.filtered) {
    return 'No instruments match the current filter.'
  }

  return 'No prices yet — the sources have not delivered any data (see SOURCES).'
}

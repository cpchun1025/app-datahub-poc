import { describe, expect, it } from 'vitest'
import { emptyPricesMessage, type EmptyPricesInput } from './emptyState'

const base: EmptyPricesInput = {
  connection: 'connected',
  hasPrices: true,
  filtered: false,
  pricesFailed: false,
  pageOutOfRange: false,
}

describe('emptyPricesMessage', () => {
  it('says loading before the first response', () => {
    expect(emptyPricesMessage({ ...base, connection: 'connecting', hasPrices: false })).toBe('Loading prices…')
  })

  it('says the backend is unreachable when nothing was ever received', () => {
    expect(emptyPricesMessage({ ...base, connection: 'disconnected', hasPrices: false })).toMatch(/unreachable/i)
  })

  it('points to the error banner when prices failed although the backend answered', () => {
    expect(emptyPricesMessage({ ...base, hasPrices: false, pricesFailed: true })).toMatch(/see the error/i)
  })

  it('explains a page that no longer exists instead of claiming there is no data', () => {
    expect(emptyPricesMessage({ ...base, pageOutOfRange: true })).toMatch(/no longer exists/i)
  })

  it('distinguishes an empty filter result from an empty store', () => {
    expect(emptyPricesMessage({ ...base, filtered: true })).toMatch(/match the current filter/i)
    expect(emptyPricesMessage(base)).toMatch(/No prices yet/)
  })
})

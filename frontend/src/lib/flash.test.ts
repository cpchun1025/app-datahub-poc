import { describe, expect, it } from 'vitest'
import type { PriceDto } from '../api/types'
import { buildBaseline, computeFlashes, pruneFlashes, rowKey, type Flash } from './flash'

function row(price: number, instrumentId = 'A', sourceId = 's1'): PriceDto {
  return {
    sourceId,
    instrumentId,
    price,
    currency: 'USD',
    sourceUpdatedAtUtc: '2026-01-01T00:00:00Z',
    receivedAtUtc: '2026-01-01T00:00:00Z',
    sourceRevision: 1,
    sourceStatus: 'Live',
  }
}

const none = new Map<string, Flash>()

describe('computeFlashes', () => {
  it('does not treat the first load (no baseline) as a price change', () => {
    const flashes = computeFlashes({ baseline: null, previous: none, rows: [row(10)], nowMs: 0, seq: 1, holdMs: 900 })
    expect(flashes.size).toBe(0)
  })

  it('flags up and down moves against the previous screen', () => {
    const baseline = buildBaseline([row(10, 'A'), row(20, 'B')])
    const flashes = computeFlashes({
      baseline,
      previous: none,
      rows: [row(11, 'A'), row(19, 'B')],
      nowMs: 5,
      seq: 7,
      holdMs: 900,
    })

    expect(flashes.get(rowKey('s1', 'A'))).toEqual({ direction: 'up', seq: 7, atMs: 5 })
    expect(flashes.get(rowKey('s1', 'B'))).toEqual({ direction: 'down', seq: 7, atMs: 5 })
  })

  it('shows only the net change between two reads (100 -> 101 -> 100 is invisible)', () => {
    const baseline = buildBaseline([row(100)])
    const flashes = computeFlashes({ baseline, previous: none, rows: [row(100)], nowMs: 0, seq: 2, holdMs: 900 })
    expect(flashes.size).toBe(0)
  })

  it('does not flag rows that were not on the previous screen', () => {
    const baseline = buildBaseline([row(10, 'A')])
    const flashes = computeFlashes({ baseline, previous: none, rows: [row(99, 'NEW')], nowMs: 0, seq: 2, holdMs: 900 })
    expect(flashes.size).toBe(0)
  })

  it('tracks the same instrument id in different sources separately', () => {
    const baseline = buildBaseline([row(10, 'X', 's1'), row(20, 'X', 's2')])
    const flashes = computeFlashes({
      baseline,
      previous: none,
      rows: [row(11, 'X', 's1'), row(19, 'X', 's2')],
      nowMs: 0,
      seq: 3,
      holdMs: 900,
    })

    expect(flashes.get(rowKey('s1', 'X'))?.direction).toBe('up')
    expect(flashes.get(rowKey('s2', 'X'))?.direction).toBe('down')
  })

  it('keeps a previous flash during the hold window and drops it afterwards', () => {
    const carried: Flash = { direction: 'up', seq: 1, atMs: 1000 }
    const previous = new Map([[rowKey('s1', 'A'), carried]])
    const baseline = buildBaseline([row(11)])

    const within = computeFlashes({ baseline, previous, rows: [row(11)], nowMs: 1500, seq: 2, holdMs: 900 })
    expect(within.get(rowKey('s1', 'A'))).toBe(carried)

    const expired = computeFlashes({ baseline, previous, rows: [row(11)], nowMs: 1900, seq: 3, holdMs: 900 })
    expect(expired.size).toBe(0)
  })

  it('replaces a carried flash when the price moves again', () => {
    const previous = new Map([[rowKey('s1', 'A'), { direction: 'up', seq: 1, atMs: 1000 } satisfies Flash]])
    const baseline = buildBaseline([row(11)])
    const flashes = computeFlashes({ baseline, previous, rows: [row(10)], nowMs: 1200, seq: 2, holdMs: 900 })

    expect(flashes.get(rowKey('s1', 'A'))).toEqual({ direction: 'down', seq: 2, atMs: 1200 })
  })
})

describe('pruneFlashes', () => {
  const fresh: Flash = { direction: 'up', seq: 2, atMs: 1800 }
  const old: Flash = { direction: 'down', seq: 1, atMs: 1000 }

  it('drops only the flashes that are older than the hold window', () => {
    const flashes = new Map([
      [rowKey('s1', 'A'), old],
      [rowKey('s1', 'B'), fresh],
    ])

    const pruned = pruneFlashes(flashes, 2000, 900)

    expect([...pruned.keys()]).toEqual([rowKey('s1', 'B')])
    expect(pruned.get(rowKey('s1', 'B'))).toBe(fresh)
  })

  it('returns the very same map when nothing has expired, so no re-render is triggered', () => {
    const flashes = new Map([[rowKey('s1', 'B'), fresh]])

    expect(pruneFlashes(flashes, 2000, 900)).toBe(flashes)
    expect(pruneFlashes(none, 2000, 900)).toBe(none)
  })
})

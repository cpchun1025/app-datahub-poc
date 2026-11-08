import { describe, expect, it } from 'vitest'
import {
  formatAgeBetween,
  formatBytes,
  formatClock,
  formatDuration,
  formatInteger,
  formatMs,
  formatPrice,
  formatUtcDateTime,
  formatUtcDateTimeMs,
  formatUtcTime,
  formatUtcTimeMs,
} from './format'

describe('formatPrice', () => {
  it('uses the configured number of decimals instead of a hard-coded two', () => {
    expect(formatPrice(12.3, 2)).toBe('12.30')
    expect(formatPrice(12.3, 3)).toBe('12.300')
    expect(formatPrice(12.3, 4)).toBe('12.3000')
    expect(formatPrice(12.6, 0)).toBe('13')
  })

  it('groups thousands', () => {
    expect(formatPrice(1234567.891, 2)).toBe('1,234,567.89')
  })

  it('shows the raw value when the precision is unknown or invalid', () => {
    expect(formatPrice(12.34567, undefined)).toBe('12.34567')
    expect(formatPrice(12.34567, null)).toBe('12.34567')
    expect(formatPrice(12.34567, -1)).toBe('12.34567')
    expect(formatPrice(12.34567, 2.5)).toBe('12.34567')
    expect(formatPrice(12.34567, 99)).toBe('12.34567')
  })

  it('shows a dash for non-finite values', () => {
    expect(formatPrice(Number.NaN, 2)).toBe('—')
    expect(formatPrice(Number.POSITIVE_INFINITY, 2)).toBe('—')
  })
})

describe('formatInteger', () => {
  it('groups thousands and tolerates missing values', () => {
    expect(formatInteger(4300)).toBe('4,300')
    expect(formatInteger(null)).toBe('—')
    expect(formatInteger(undefined)).toBe('—')
  })
})

describe('UTC time formatting', () => {
  const iso = '2026-11-05T17:52:19.123Z'

  it('formats time, time with milliseconds and date-time', () => {
    expect(formatUtcTime(iso)).toBe('17:52:19')
    expect(formatUtcTimeMs(iso)).toBe('17:52:19.123')
    expect(formatUtcDateTime(iso)).toBe('2026-11-05 17:52:19')
    expect(formatUtcDateTimeMs(iso)).toBe('2026-11-05 17:52:19.123')
    expect(formatClock(Date.parse(iso))).toBe('2026-11-05 17:52:19')
  })

  it('shows a dash for missing or invalid input', () => {
    expect(formatUtcTime(null)).toBe('—')
    expect(formatUtcTime(undefined)).toBe('—')
    expect(formatUtcTime('')).toBe('—')
    expect(formatUtcDateTime('not a date')).toBe('—')
    expect(formatUtcDateTimeMs('not a date')).toBe('—')
  })
})

describe('formatDuration', () => {
  it('formats short, minute, hour and day spans', () => {
    expect(formatDuration(0)).toBe('<1s')
    expect(formatDuration(999)).toBe('<1s')
    expect(formatDuration(12_000)).toBe('12s')
    expect(formatDuration(185_000)).toBe('3m 05s')
    expect(formatDuration(7_800_000)).toBe('2h 10m')
    expect(formatDuration(4 * 86_400_000 + 3 * 3_600_000)).toBe('4d 03h')
  })

  it('never shows negative durations (clock skew)', () => {
    expect(formatDuration(-5000)).toBe('<1s')
  })
})

describe('formatAgeBetween', () => {
  it('measures against the supplied (server) time', () => {
    expect(formatAgeBetween('2026-01-01T00:00:00Z', '2026-01-01T00:00:12Z')).toBe('12s')
    expect(formatAgeBetween(null, '2026-01-01T00:00:12Z')).toBe('—')
    expect(formatAgeBetween('2026-01-01T00:00:00Z', 'nope')).toBe('—')
  })
})

describe('formatBytes / formatMs', () => {
  it('formats sizes', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2048)).toBe('2.0 KB')
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB')
    expect(formatBytes(null)).toBe('—')
    expect(formatBytes(-1)).toBe('—')
  })

  it('formats durations in ms', () => {
    expect(formatMs(31.4)).toBe('31 ms')
    expect(formatMs(1500)).toBe('1.5 s')
    expect(formatMs(null)).toBe('—')
  })
})

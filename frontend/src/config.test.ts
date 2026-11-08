import { describe, expect, it } from 'vitest'
import { DEFAULT_POLL_INTERVAL_MS, MAX_POLL_INTERVAL_MS, MIN_POLL_INTERVAL_MS, normalizeBaseUrl, parsePollInterval } from './config'

describe('parsePollInterval', () => {
  it('falls back to the default for missing or invalid values', () => {
    expect(parsePollInterval(undefined)).toBe(DEFAULT_POLL_INTERVAL_MS)
    expect(parsePollInterval('')).toBe(DEFAULT_POLL_INTERVAL_MS)
    expect(parsePollInterval('   ')).toBe(DEFAULT_POLL_INTERVAL_MS)
    expect(parsePollInterval('abc')).toBe(DEFAULT_POLL_INTERVAL_MS)
    expect(parsePollInterval('Infinity')).toBe(DEFAULT_POLL_INTERVAL_MS)
  })

  it('accepts a valid value and truncates fractions', () => {
    expect(parsePollInterval('2000')).toBe(2000)
    expect(parsePollInterval('1500.9')).toBe(1500)
  })

  it('clamps to a sane range so a typo cannot hammer the backend', () => {
    expect(parsePollInterval('0')).toBe(MIN_POLL_INTERVAL_MS)
    expect(parsePollInterval('-100')).toBe(MIN_POLL_INTERVAL_MS)
    expect(parsePollInterval('99999999')).toBe(MAX_POLL_INTERVAL_MS)
  })
})

describe('normalizeBaseUrl', () => {
  it('trims and removes trailing slashes', () => {
    expect(normalizeBaseUrl(undefined)).toBe('')
    expect(normalizeBaseUrl('  http://localhost:5080/// ')).toBe('http://localhost:5080')
    expect(normalizeBaseUrl('')).toBe('')
  })
})

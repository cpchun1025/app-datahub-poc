import { describe, expect, it } from 'vitest'
import { publishedLabel, safeNewsUrl } from './news'

describe('news rendering helpers', () => {
  it('only accepts absolute HTTP(S) links', () => {
    expect(safeNewsUrl('https://example.com/article')).toBe('https://example.com/article')
    expect(safeNewsUrl('javascript:alert(1)')).toBeNull()
    expect(safeNewsUrl('/relative')).toBeNull()
  })

  it('does not invent a publication time', () => {
    expect(publishedLabel({ publishedAtUtc: null, publishedDate: null })).toBe('Unknown')
    expect(publishedLabel({ publishedAtUtc: null, publishedDate: '2026-01-01' })).toBe('2026-01-01 (date only)')
    expect(publishedLabel({ publishedAtUtc: '2026-01-01T12:00:00Z', publishedDate: null })).toBe('2026-01-01T12:00:00Z')
  })
})

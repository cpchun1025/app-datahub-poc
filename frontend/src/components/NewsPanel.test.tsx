import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { NewsTable, type NewsItem } from './NewsPanel'

const item: NewsItem = {
  sourceId: 'mock-news', articleId: '1', title: '<script>test</script>',
  url: 'https://example.com/a', publisher: 'Mock', originalPublisher: null, category: null,
  publishedAtUtc: null, publishedDate: '2026-01-01', publicationTimePrecision: 'Date',
  originalPublicationTime: '2026-01-01', firstSeenAtUtc: '2026-01-02T00:00:00Z',
  fetchedAtUtc: '2026-01-02T00:00:00Z', language: 'en', mappingWarnings: [],
}
const render = (items: NewsItem[] | null) => renderToStaticMarkup(
  <NewsTable items={items} direction="desc" onDirection={() => {}} onSelect={() => {}} />,
)

describe('news table', () => {
  it('escapes headlines and opens only safe links with secure rel', () => {
    const html = render([item])
    expect(html).toContain('&lt;script&gt;test&lt;/script&gt;')
    expect(html).not.toContain('<script>')
    expect(html).toContain('target="_blank" rel="noopener noreferrer"')
    expect(html).toContain('2026-01-01 (date only)')
    expect(html).toContain('aria-sort="descending"')
    expect(render([{ ...item, url: 'javascript:alert(1)' }])).not.toContain('href=')
  })

  it('distinguishes loading from empty', () => {
    expect(render(null)).toContain('Loading news')
    expect(render([])).toContain('No matching news')
    const unavailable = renderToStaticMarkup(
      <NewsTable items={null} direction="desc" onDirection={() => {}} onSelect={() => {}} unavailable />,
    )
    expect(unavailable).toContain('News unavailable; retrying')
    expect(unavailable).not.toContain('Loading news')
  })
})

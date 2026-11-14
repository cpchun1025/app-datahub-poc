import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { NewsSourcePanel, type NewsSourceStatus } from './NewsSourcePanel'

const source: NewsSourceStatus = {
  sourceId: 'mock-news', state: 'Connected', cachedItemCount: 3,
  lastAttemptAtUtc: '2026-01-03T00:00:00Z', lastSuccessfulFetchAtUtc: '2026-01-03T00:00:00Z',
  latestArticlePublishedAtUtc: '2026-01-01T00:00:00Z', lastError: null,
}
const render = (overrides: Partial<Parameters<typeof NewsSourcePanel>[0]> = {}) => renderToStaticMarkup(
  <NewsSourcePanel source={source} unavailable={false} adminEnabled busy={null} actionResult={null}
    onOutage={() => {}} {...overrides} />,
)

describe('news source overview', () => {
  it('shows fetch and publication times separately with outage controls', () => {
    const html = render()
    expect(html).toContain('CONNECTED')
    expect(html).toContain('LAST ATTEMPT UTC')
    expect(html).toContain('LAST SUCCESS UTC')
    expect(html).toContain('LATEST PUBLICATION UTC')
    expect(html).toContain('Outage 20s')
    expect(html).toContain('End outage')
    expect(html).not.toContain('disabled=""')
  })

  it('shows source failure and disables actions while busy or unavailable', () => {
    const disconnected = render({ source: { ...source, state: 'Disconnected', lastError: 'Simulated outage' }, busy: 'outage' })
    expect(disconnected).toContain('DISCONNECTED')
    expect(disconnected).toContain('Simulated outage')
    expect(disconnected.match(/disabled=""/g)).toHaveLength(2)
    const unavailable = render({ unavailable: true })
    expect(unavailable).toContain('DISCONNECTED')
    expect(unavailable).not.toContain('>CONNECTED<')
    expect(unavailable.match(/disabled=""/g)).toHaveLength(2)
  })

  it('distinguishes recovery and action errors', () => {
    const html = render({ source: { ...source, state: 'Recovering' },
      actionResult: { ok: false, text: 'Request failed', atMs: 0 } })
    expect(html).toContain('RECOVERING')
    expect(html).toContain('ERROR')
    expect(html).toContain('Request failed')
  })
})

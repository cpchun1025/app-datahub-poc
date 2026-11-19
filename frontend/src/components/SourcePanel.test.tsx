import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { SourceDto } from '../api/types'
import { filterSources } from '../lib/sourceFilters'
import { SourcePanel } from './SourcePanel'

function source(index: number): SourceDto {
  const isCsv = index % 3 === 0
  return {
    sourceId: isCsv ? `demo-csv-${index}` : `mock-${index}`,
    displayName: `Synthetic source ${index}`,
    kind: isCsv ? 'Csv' : index % 2 === 0 ? 'Polling' : 'Streaming',
    isMock: !isCsv,
    isSynthetic: true,
    description: 'Synthetic fixture',
    priceDecimals: 4,
    status: index % 5 === 0 ? 'Disconnected' : index % 2 === 0 ? 'Live' : 'Stale',
    recordCount: 20,
    restoredFromCheckpoint: false,
    syncedSinceStart: true,
    snapshotInProgress: false,
    lastSnapshotAtUtc: null,
    lastUpdateReceivedAtUtc: null,
    lastFeedActivityAtUtc: null,
    nextSnapshotDueAtUtc: null,
    lastError: index === 0 ? 'fixture failure' : null,
    lastErrorAtUtc: null,
    csvImport: null,
  }
}

describe('source monitoring panel', () => {
  const sources = Array.from({ length: 30 }, (_, index) => source(index))

  it('keeps a compact accessible summary for large source lists', () => {
    const html = renderToStaticMarkup(
      <SourcePanel sources={sources} serverTimeUtc={null} backendDown={false} sourceFilter={null}
        onToggleSource={() => {}} adminEnabled={false} busy={null} onOutage={() => {}} />,
    )
    expect(html).toContain('30 total')
    expect(html).toContain('1 WARNING')
    expect(html).toContain('aria-expanded="false"')
    expect(html).toContain('aria-controls="source-monitor-content"')
    expect(html).not.toContain('Search sources')
  })

  it('filters 30 monitoring fixtures independently by name, adapter kind and health', () => {
    expect(filterSources(sources, '', '', '')).toHaveLength(30)
    expect(filterSources(sources, 'DEMO-CSV-3', '', '')).toHaveLength(1)
    expect(filterSources(sources, '', 'Csv', 'Live').map((item) => item.sourceId)).toEqual([
      'demo-csv-6', 'demo-csv-12', 'demo-csv-18', 'demo-csv-24',
    ])
    expect(filterSources(sources, 'mock-', 'Polling', 'Disconnected').map((item) => item.sourceId))
      .toEqual(['mock-10', 'mock-20'])
  })
})

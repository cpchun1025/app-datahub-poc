import { useEffect, useState } from 'react'
import { request } from '../api/client'
import { config } from '../config'
import { Pagination } from './Pagination'
import { publishedLabel, safeNewsUrl } from '../lib/news'
import { NewsSourcePanel, type NewsSourceStatus } from './NewsSourcePanel'
import type { ActionKind, ActionResult } from '../hooks/useAdminActions'

export interface NewsItem {
  sourceId: string
  articleId: string
  title: string
  url: string
  publisher: string
  originalPublisher: string | null
  category: string | null
  publishedAtUtc: string | null
  publishedDate: string | null
  publicationTimePrecision: 'Unknown' | 'Date' | 'Instant'
  originalPublicationTime: string | null
  firstSeenAtUtc: string
  fetchedAtUtc: string
  language: string
  mappingWarnings: string[]
}

interface NewsResponse {
  items: NewsItem[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  source: NewsSourceStatus
}

export function NewsTable({ items, direction, onDirection, onSelect, unavailable = false }: {
  items: NewsItem[] | null
  direction: string
  onDirection: () => void
  onSelect: (key: string) => void
  unavailable?: boolean
}) {
  return <table className="grid">
    <thead><tr>
      <th scope="col" aria-sort={direction === 'asc' ? 'ascending' : 'descending'}>
        <button className="sort-header" onClick={onDirection}>PUBLISHED {direction === 'asc' ? '▲' : '▼'}</button>
      </th>
      <th scope="col">SOURCE</th><th scope="col">CATEGORY</th><th scope="col">HEADLINE</th>
    </tr></thead>
    <tbody>{!items ? <tr><td colSpan={4}>{unavailable ? 'News unavailable; retrying…' : 'Loading news…'}</td></tr>
      : items.length === 0 ? <tr><td colSpan={4}>No matching news.</td></tr>
        : items.map(row => {
          const key = `${row.sourceId}\u0000${row.articleId}`
          const url = safeNewsUrl(row.url)
          return <tr key={key}>
            <td>{publishedLabel(row)}</td><td>{row.sourceId}</td><td>{row.category ?? '—'}</td>
            <td><button className="link" onClick={() => onSelect(key)} aria-label={`Details: ${row.title}`}>DETAILS</button>{' '}
              {url ? <a href={url} target="_blank" rel="noopener noreferrer">{row.title}</a> : row.title}</td>
          </tr>
        })}</tbody>
  </table>
}

export function NewsPanel({ adminEnabled, backendDown, busy, onOutage, actionResult }: {
  adminEnabled: boolean
  backendDown: boolean
  busy: ActionKind | null
  onOutage: (seconds: number) => void
  actionResult: ActionResult | null
}) {
  const [keyword, setKeyword] = useState('')
  const [category, setCategory] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [direction, setDirection] = useState('desc')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState<number>(config.defaultPageSize)
  const [response, setResponse] = useState<{ query: string; data: NewsResponse } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize), sortDirection: direction })
  if (keyword) query.set('keyword', keyword)
  if (category) query.set('category', category)
  if (from) query.set('publishedFrom', from)
  if (to) query.set('publishedTo', to)
  const queryString = query.toString()
  const data = response?.query === queryString ? response.data : null

  useEffect(() => {
    const controller = new AbortController()
    const timer: { current?: ReturnType<typeof setTimeout> } = {}
    async function poll() {
      try {
        const result = await request<NewsResponse>(`/api/news?${queryString}`, { signal: controller.signal })
        if (!controller.signal.aborted) {
          setResponse({ query: queryString, data: result })
          setError(null)
        }
      } catch (reason) {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'News request failed')
      } finally {
        if (!controller.signal.aborted) timer.current = setTimeout(poll, config.pollIntervalMs)
      }
    }
    void poll()
    return () => { controller.abort(); if (timer.current !== undefined) clearTimeout(timer.current) }
  }, [queryString])

  const item = data?.items.find(row => `${row.sourceId}\u0000${row.articleId}` === selected)
  const change = (setter: (value: string) => void, value: string) => { setter(value); setPage(1); setSelected(null) }
  const range = data && data.items.length > 0
    ? { from: (data.page - 1) * data.pageSize + 1, to: (data.page - 1) * data.pageSize + data.items.length } : null
  return (
    <main className="panel news" aria-labelledby="news-title">
      <h2 id="news-title" className="panel-title">NEWS <span className="panel-note">Synthetic metadata · bounded cache · not a history</span></h2>
      <div className="news-controls">
        <label>KEYWORD <input className="ctl" value={keyword} onChange={e => change(setKeyword, e.target.value)} /></label>
        <label>CATEGORY <input className="ctl" value={category} onChange={e => change(setCategory, e.target.value)} /></label>
        <label>FROM <input className="ctl" type="date" value={from} onChange={e => change(setFrom, e.target.value)} /></label>
        <label>TO <input className="ctl" type="date" value={to} onChange={e => change(setTo, e.target.value)} /></label>
      </div>
      {error ? <p className="err" role="alert">NEWS ERROR: {error}</p> : null}
      <NewsSourcePanel source={response?.data.source ?? null} unavailable={backendDown || error !== null}
        adminEnabled={adminEnabled} busy={busy} onOutage={onOutage} actionResult={actionResult} />
      <div className="news-scroll">
        <NewsTable items={data?.items ?? null} direction={direction}
          unavailable={backendDown || error !== null}
          onDirection={() => change(setDirection, direction === 'asc' ? 'desc' : 'asc')} onSelect={setSelected} />
      </div>
      {item ? <section className="news-detail" aria-label="News details">
        <strong>{item.title}</strong>
        <div>ID {item.sourceId} / {item.articleId} · PUBLISHER {item.publisher} · ORIGINAL {item.originalPublisher ?? 'Unknown'} · LANGUAGE {item.language}</div>
        <div>PUBLICATION {publishedLabel(item)} · PRECISION {item.publicationTimePrecision} · ORIGINAL {item.originalPublicationTime ?? 'Unknown'}</div>
        <div>FIRST SEEN {item.firstSeenAtUtc} · FETCHED {item.fetchedAtUtc}</div>
        <div>WARNINGS {item.mappingWarnings.length ? item.mappingWarnings.join('; ') : 'None'}</div>
      </section> : null}
      <Pagination page={page} pageSize={pageSize} pageSizes={config.pageSizes}
        totalCount={data?.totalCount ?? null} totalPages={data?.totalPages ?? null} range={range} loading={false}
        onPage={value => { setPage(value); setSelected(null) }}
        onPageSize={value => { setPageSize(value); setPage(1); setSelected(null) }} />
    </main>
  )
}

import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  api,
  buildPricesPath,
  buildPricesExportPath,
  describeApiError,
  isAbortError,
  priceQueryKey,
  request,
  settle,
  type PriceQuery,
} from './client'

function stubFetch(handler: (url: string, init: RequestInit) => Promise<Response>) {
  const fn = vi.fn((url: string | URL | Request, init?: RequestInit) => handler(String(url), init ?? {}))
  vi.stubGlobal('fetch', fn)
  return fn
}

function json(body: unknown, init: ResponseInit = {}, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), {
    ...init,
    headers: { 'content-type': contentType, ...(init.headers as Record<string, string> | undefined) },
  })
}

const signal = () => new AbortController().signal

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('request', () => {
  it('returns parsed JSON and sends a no-store GET for the same-origin path', async () => {
    const fetchMock = stubFetch(async () => json({ status: 'ok' }))

    await expect(request('/api/health', { signal: signal() })).resolves.toEqual({ status: 'ok' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/health')
    expect(init).toMatchObject({ method: 'GET', cache: 'no-store' })
  })

  it('classifies a network failure as unreachable', async () => {
    stubFetch(async () => {
      throw new TypeError('Failed to fetch')
    })

    await expect(request('/api/health', { signal: signal() })).rejects.toMatchObject({
      name: 'ApiError',
      kind: 'unreachable',
      message: 'Failed to fetch',
    })
  })

  it.each([500, 502, 503, 504])('classifies HTTP %i without problem+json as unreachable (proxy/gateway)', async (status) => {
    stubFetch(async () => new Response('', { status, headers: { 'content-type': 'text/html' } }))

    await expect(request('/api/health', { signal: signal() })).rejects.toMatchObject({ kind: 'unreachable', status })
  })

  it('keeps a 500 with problem+json as an API problem (the backend answered)', async () => {
    stubFetch(async () => json({ title: 'An error occurred while processing your request.', status: 500 }, { status: 500 }, 'application/problem+json'))

    await expect(request('/api/health', { signal: signal() })).rejects.toMatchObject({
      kind: 'problem',
      status: 500,
      problem: { title: 'An error occurred while processing your request.' },
    })
  })

  it('exposes validation errors from problem+json', async () => {
    stubFetch(async () =>
      json(
        { title: 'One or more validation errors occurred.', status: 400, errors: { pageSize: ['pageSize must be between 1 and 500.'] } },
        { status: 400 },
        'application/problem+json',
      ),
    )

    const error = await request('/api/prices', { signal: signal() }).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect(describeApiError(error)).toBe(
      'One or more validation errors occurred.; pageSize: pageSize must be between 1 and 500.',
    )
  })

  it('treats other non-OK responses as http errors', async () => {
    stubFetch(async () => new Response('missing', { status: 404, statusText: 'Not Found', headers: { 'content-type': 'text/plain' } }))

    await expect(request('/nope', { signal: signal() })).rejects.toMatchObject({ kind: 'http', status: 404, message: 'HTTP 404 Not Found' })
  })

  it('reports an unreadable success body as an http error, not a disconnection', async () => {
    stubFetch(async () => new Response('<html>', { status: 200, headers: { 'content-type': 'application/json' } }))

    await expect(request('/api/health', { signal: signal() })).rejects.toMatchObject({ kind: 'http' })
  })

  it('rethrows our own cancellation untouched (it is not a disconnection)', async () => {
    stubFetch(
      (_url, init) =>
        new Promise<Response>((_, reject) => {
          init.signal?.addEventListener('abort', () => reject(init.signal?.reason))
        }),
    )
    const controller = new AbortController()
    const pending = request('/api/health', { signal: controller.signal })

    controller.abort()

    const error = await pending.catch((e: unknown) => e)
    expect(error).not.toBeInstanceOf(ApiError)
    expect(isAbortError(error)).toBe(true)
  })

  it('turns a hung request into an unreachable timeout', async () => {
    stubFetch(
      (_url, init) =>
        new Promise<Response>((_, reject) => {
          init.signal?.addEventListener('abort', () => reject(init.signal?.reason))
        }),
    )

    await expect(request('/api/health', { signal: signal(), timeoutMs: 20 })).rejects.toMatchObject({
      kind: 'unreachable',
      message: 'Request timed out after 20 ms',
    })
  })
})

describe('settle', () => {
  it('wraps success and ApiError, but rethrows when our signal was aborted', async () => {
    const live = signal()
    await expect(settle(Promise.resolve(1), live)).resolves.toEqual({ ok: true, value: 1 })

    const failure = new ApiError('problem', 'nope', 409)
    await expect(settle(Promise.reject(failure), live)).resolves.toEqual({ ok: false, error: failure })

    const controller = new AbortController()
    controller.abort()
    const cancelled = new DOMException('aborted', 'AbortError')
    await expect(settle(Promise.reject(cancelled), controller.signal)).rejects.toBe(cancelled)
  })

  it('reports unexpected errors as http-level problems so bugs are not shown as a disconnection', async () => {
    const result = await settle(Promise.reject(new Error('kaboom')), signal())

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.kind).toBe('http')
      expect(result.error.message).toBe('kaboom')
    }
  })
})

describe('buildPricesPath', () => {
  const base: PriceQuery = { sourceId: null, instrument: '', page: 1, pageSize: 100 }

  it('sends only paging when there is no filter', () => {
    expect(buildPricesPath(base)).toBe('/api/prices?page=1&pageSize=100')
  })

  it('adds the source filter and a trimmed, encoded instrument search', () => {
    expect(buildPricesPath({ ...base, sourceId: 'mock-api-c', instrument: '  A&B 1 ', page: 3, pageSize: 50 })).toBe(
      '/api/prices?sourceId=mock-api-c&instrumentId=A%26B+1&page=3&pageSize=50',
    )
  })

  it('uses the path as the query key, so any change produces a new key', () => {
    expect(priceQueryKey(base)).toBe(priceQueryKey({ ...base }))
    expect(priceQueryKey(base)).not.toBe(priceQueryKey({ ...base, page: 2 }))
    expect(priceQueryKey(base)).not.toBe(priceQueryKey({ ...base, sourceId: 'x' }))
  })
})

describe('api admin calls', () => {
  it('POSTs to the encoded admin routes', async () => {
    const fetchMock = stubFetch(async () => json({ ok: true }))

    await api.refreshSnapshot('mock stream/a', signal())
    await api.saveCheckpoint(signal())
    await api.simulateOutage('mock-a', 20, signal())

    const calls = fetchMock.mock.calls.map(([url, init]) => [String(url), init?.method])
    expect(calls).toEqual([
      ['/api/admin/snapshot/mock%20stream%2Fa', 'POST'],
      ['/api/admin/checkpoint', 'POST'],
      ['/api/admin/mock/mock-a/outage?seconds=20', 'POST'],
    ])
  })

  describe('grid queries and CSV', () => {
    const query: PriceQuery = {
      sourceId: 'alpha', instrument: 'A&B', page: 3, pageSize: 25, sortBy: 'price', sortDirection: 'desc',
      currency: 'USD', sourceStatus: 'Live', minPrice: '-12.50', maxPrice: '100.12345678',
    }

    it('keeps decimal input intact and includes every filter/sort in the key', () => {
      const params = new URLSearchParams(buildPricesPath(query).split('?')[1])
      expect(params.get('minPrice')).toBe('-12.50')
      expect(params.get('maxPrice')).toBe('100.12345678')
      for (const field of ['currency', 'sourceStatus', 'minPrice', 'maxPrice', 'sortBy', 'sortDirection'] as const) {
        expect(priceQueryKey(query)).not.toBe(priceQueryKey({ ...query, [field]: undefined }))
      }
    })

    it('shares table parameters but drops pagination for all-matching export', () => {
      const table = new URLSearchParams(buildPricesPath(query).split('?')[1])
      const page = new URLSearchParams(buildPricesExportPath(query, 'page').split('?')[1])
      page.delete('scope')
      expect(page.toString()).toBe(table.toString())
      const all = new URLSearchParams(buildPricesExportPath(query, 'all').split('?')[1])
      expect(all.get('scope')).toBe('all')
      expect(all.has('page')).toBe(false)
      expect(all.has('pageSize')).toBe(false)
      expect(all.get('sortBy')).toBe('price')
      expect(all.get('instrumentId')).toBe('A&B')
    })

    it('returns a CSV blob, not parsed JSON', async () => {
      stubFetch(async () => new Response('csv', { headers: { 'content-type': 'text/csv; charset=utf-8' } }))
      const blob = await api.exportPrices(query, 'page', signal())
      expect(await blob.text()).toBe('csv')
    })

    it('reports export limit problems instead of downloading an error document', async () => {
      stubFetch(async () => json({ title: 'Export row limit exceeded', status: 422 }, { status: 422 }, 'application/problem+json'))
      await expect(api.exportPrices(query, 'all', signal())).rejects.toMatchObject({ kind: 'problem', status: 422 })
    })

    it('rejects an unexpected HTML success response', async () => {
      stubFetch(async () => new Response('html', { headers: { 'content-type': 'text/html' } }))
      await expect(api.exportPrices(query, 'all', signal())).rejects.toMatchObject({ kind: 'http', message: 'Expected a CSV export response' })
    })
  })
})

describe('describeApiError', () => {
  it('joins title and detail, and falls back to the message for other errors', () => {
    const withDetail = new ApiError('problem', 'Source snapshot failed', 502, {
      title: 'Source snapshot failed',
      detail: 'upstream down',
    })
    expect(describeApiError(withDetail)).toBe('Source snapshot failed — upstream down')
    expect(describeApiError(new ApiError('unreachable', 'Failed to fetch'))).toBe('Failed to fetch')
    expect(describeApiError(new Error('plain'))).toBe('plain')
    expect(describeApiError('text')).toBe('text')
  })
})

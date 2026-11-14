import { config } from '../config'
import type {
  CheckpointRunResponse,
  HealthResponse,
  MockOutageResponse,
  PriceListResponse,
  ProblemDetails,
  SnapshotRefreshResponse,
  SourceListResponse,
  SourceStatus,
} from './types'

/**
 * unreachable：連唔到 backend（network error、timeout、或者 proxy/gateway 回 500/502/503/504 而冇 problem+json）。
 * problem：backend 有回應，並以 application/problem+json 說明錯誤（validation、404、409、502 source failed…）。
 * http：backend 或中間層有回應，但唔係預期格式。
 */
export type ApiErrorKind = 'unreachable' | 'problem' | 'http'

export class ApiError extends Error {
  readonly kind: ApiErrorKind
  readonly status: number | null
  readonly problem: ProblemDetails | null

  constructor(kind: ApiErrorKind, message: string, status: number | null = null, problem: ProblemDetails | null = null) {
    super(message)
    this.name = 'ApiError'
    this.kind = kind
    this.status = status
    this.problem = problem
  }
}

export type Settled<T> = { ok: true; value: T } | { ok: false; error: ApiError }

export interface PriceQuery {
  sourceId: string | null
  instrument: string
  page: number
  pageSize: number
  sortBy?: PriceSortField
  sortDirection?: SortDirection
  currency?: string
  sourceStatus?: SourceStatus | ''
  minPrice?: string
  maxPrice?: string
}

export type PriceSortField = 'sourceId' | 'instrumentId' | 'price' | 'currency' | 'sourceUpdatedAtUtc' | 'receivedAtUtc'
export type SortDirection = 'asc' | 'desc'
export type ExportScope = 'page' | 'all'

export const DEFAULT_TIMEOUT_MS = 5000
export const ACTION_TIMEOUT_MS = 30_000

const GATEWAY_STATUSES: ReadonlySet<number> = new Set([500, 502, 503, 504])

export interface RequestOptions {
  method?: 'GET' | 'POST'
  signal: AbortSignal
  timeoutMs?: number
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

async function requestResponse(path: string, options: RequestOptions): Promise<Response> {
  const { method = 'GET', signal, timeoutMs = DEFAULT_TIMEOUT_MS } = options
  const combined = AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)])

  let response: Response
  try {
    response = await fetch(`${config.apiBaseUrl}${path}`, {
      method,
      signal: combined,
      cache: 'no-store',
      headers: { Accept: 'application/json, application/problem+json, text/csv' },
    })
  } catch (error) {
    // 自己取消（unmount、換 query）要原樣拋出，等 caller 識得靜靜地丟棄，而唔係當成斷線。
    if (signal.aborted) {
      throw error
    }

    if (error instanceof DOMException && error.name === 'TimeoutError') {
      throw new ApiError('unreachable', `Request timed out after ${timeoutMs} ms`)
    }

    throw new ApiError('unreachable', error instanceof Error ? error.message : 'Network error')
  }

  const contentType = response.headers.get('content-type') ?? ''

  try {
    if (response.ok) {
      return response
    }

    if (contentType.includes('application/problem+json')) {
      const problem = await readProblem(response)
      throw new ApiError('problem', problem?.title ?? `HTTP ${response.status}`, response.status, problem)
    }
  } catch (error) {
    if (error instanceof ApiError) {
      throw error
    }

    if (signal.aborted) {
      throw error
    }

    throw new ApiError('http', 'Response body could not be read', response.status)
  }

  if (GATEWAY_STATUSES.has(response.status)) {
    throw new ApiError(
      'unreachable',
      `HTTP ${response.status}: backend unreachable through the proxy`,
      response.status,
    )
  }

  throw new ApiError('http', `HTTP ${response.status} ${response.statusText}`.trim(), response.status)
}

export async function request<T>(path: string, options: RequestOptions): Promise<T> {
  const response = await requestResponse(path, options)
  try {
    return (await response.json()) as T
  } catch (error) {
    if (options.signal.aborted) throw error
    throw new ApiError('http', 'Response body could not be read', response.status)
  }
}

async function exportPrices(query: PriceQuery, scope: ExportScope, signal: AbortSignal): Promise<Blob> {
  const response = await requestResponse(buildPricesExportPath(query, scope), { signal, timeoutMs: ACTION_TIMEOUT_MS })
  if (!response.headers.get('content-type')?.startsWith('text/csv')) {
    throw new ApiError('http', 'Expected a CSV export response', response.status)
  }
  try {
    return await response.blob()
  } catch (error) {
    if (signal.aborted) throw error
    throw new ApiError('http', 'CSV response body could not be read', response.status)
  }
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    const body: unknown = await response.json()
    return typeof body === 'object' && body !== null ? (body as ProblemDetails) : null
  } catch {
    return null
  }
}

/** 將單一 request 嘅結果收成 Settled；自己取消會繼續拋出，其他非 ApiError 當成 http 層錯誤（避免將程式 bug 誤報為斷線）。 */
export async function settle<T>(promise: Promise<T>, signal: AbortSignal): Promise<Settled<T>> {
  try {
    return { ok: true, value: await promise }
  } catch (error) {
    if (signal.aborted) {
      throw error
    }

    if (error instanceof ApiError) {
      return { ok: false, error }
    }

    return { ok: false, error: new ApiError('http', error instanceof Error ? error.message : 'Unexpected error') }
  }
}

export function describeApiError(error: unknown): string {
  if (error instanceof ApiError) {
    const problem = error.problem
    if (problem) {
      const parts = [problem.title ?? error.message]
      if (problem.detail) {
        parts.push(problem.detail)
      }

      const validation = Object.entries(problem.errors ?? {}).flatMap(([field, messages]) =>
        messages.map((message) => `${field}: ${message}`),
      )
      return [parts.join(' — '), ...validation].join('; ')
    }

    return error.message
  }

  return error instanceof Error ? error.message : String(error)
}

export function buildPricesPath(query: PriceQuery): string {
  const params = new URLSearchParams()
  if (query.sourceId) {
    params.set('sourceId', query.sourceId)
  }

  const instrument = query.instrument.trim()
  if (instrument) {
    params.set('instrumentId', instrument)
  }

  params.set('page', String(query.page))
  params.set('pageSize', String(query.pageSize))
  for (const field of ['sortBy', 'sortDirection', 'currency', 'sourceStatus', 'minPrice', 'maxPrice'] as const) {
    const value = query[field]?.trim()
    if (value) params.set(field, value)
  }
  return `/api/prices?${params.toString()}`
}

export function buildPricesExportPath(query: PriceQuery, scope: ExportScope): string {
  const params = new URLSearchParams(buildPricesPath(query).split('?')[1])
  if (scope === 'all') {
    params.delete('page')
    params.delete('pageSize')
  }
  params.set('scope', scope)
  return `/api/prices/export.csv?${params.toString()}`
}

/** 同一個 query 一定有同一個 key；換 filter／頁碼就會有新 key。 */
export function priceQueryKey(query: PriceQuery): string {
  return buildPricesPath(query)
}

export const api = {
  exportPrices,
  health: (signal: AbortSignal, timeoutMs?: number) =>
    request<HealthResponse>('/api/health', { signal, timeoutMs }),

  sources: (signal: AbortSignal, timeoutMs?: number) =>
    request<SourceListResponse>('/api/sources', { signal, timeoutMs }),

  prices: (query: PriceQuery, signal: AbortSignal, timeoutMs?: number) =>
    request<PriceListResponse>(buildPricesPath(query), { signal, timeoutMs }),

  refreshSnapshot: (sourceId: string, signal: AbortSignal) =>
    request<SnapshotRefreshResponse>(`/api/admin/snapshot/${encodeURIComponent(sourceId)}`, {
      method: 'POST',
      signal,
      timeoutMs: ACTION_TIMEOUT_MS,
    }),

  saveCheckpoint: (signal: AbortSignal) =>
    request<CheckpointRunResponse>('/api/admin/checkpoint', {
      method: 'POST',
      signal,
      timeoutMs: ACTION_TIMEOUT_MS,
    }),

  simulateOutage: (sourceId: string, seconds: number, signal: AbortSignal) =>
    request<MockOutageResponse>(
      `/api/admin/mock/${encodeURIComponent(sourceId)}/outage?seconds=${seconds}`,
      { method: 'POST', signal, timeoutMs: ACTION_TIMEOUT_MS },
    ),
  simulateNewsOutage: (seconds: number, signal: AbortSignal) =>
    request<MockOutageResponse>(`/api/admin/mock-news/outage?seconds=${seconds}`,
      { method: 'POST', signal, timeoutMs: ACTION_TIMEOUT_MS }),
}

// 對應 backend DataHub.Api.Endpoints.Dtos。JSON 係 camelCase、enum 係字串、時間係 UTC ISO-8601（結尾 Z）。

export type SourceStatus = 'Initializing' | 'Live' | 'Recovering' | 'Stale' | 'Disconnected'
export type SourceKind = 'Streaming' | 'Polling'

export interface PriceDto {
  sourceId: string
  instrumentId: string
  price: number
  currency: string
  sourceUpdatedAtUtc: string
  receivedAtUtc: string
  sourceRevision: number
  sourceStatus: SourceStatus
}

export interface SourceFreshnessDto {
  sourceId: string
  status: SourceStatus
  syncedSinceStart: boolean
  lastUpdateReceivedAtUtc: string | null
}

export interface PriceListResponse {
  serverTimeUtc: string
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  items: PriceDto[]
  sources: SourceFreshnessDto[]
}

export interface SourceDto {
  sourceId: string
  displayName: string
  kind: SourceKind
  isMock: boolean
  description: string
  priceDecimals: number
  status: SourceStatus
  recordCount: number
  restoredFromCheckpoint: boolean
  syncedSinceStart: boolean
  snapshotInProgress: boolean
  lastSnapshotAtUtc: string | null
  lastUpdateReceivedAtUtc: string | null
  lastFeedActivityAtUtc: string | null
  nextSnapshotDueAtUtc: string | null
  lastError: string | null
  lastErrorAtUtc: string | null
}

export interface SourceListResponse {
  serverTimeUtc: string
  sources: SourceDto[]
}

export interface SourceSummaryDto {
  total: number
  live: number
  initializing: number
  recovering: number
  stale: number
  disconnected: number
}

export interface CheckpointDto {
  enabled: boolean
  loadedFrom: string | null
  loadedSavedAtUtc: string | null
  loadedRecordCount: number | null
  loadWarnings: string[]
  saveInProgress: boolean
  lastAttemptAtUtc: string | null
  lastSuccessAtUtc: string | null
  lastSuccessRecordCount: number | null
  lastSuccessBytes: number | null
  lastSuccessDurationMs: number | null
  lastErrorAtUtc: string | null
  lastError: string | null
  consecutiveFailures: number
}

export interface HealthResponse {
  status: string
  serverTimeUtc: string
  startedAtUtc: string
  mockMode: boolean
  adminEnabled: boolean
  sources: SourceSummaryDto
  checkpoint: CheckpointDto
}

export interface SnapshotRefreshResponse {
  sourceId: string
  added: number
  updated: number
  removed: number
  keptNewer: number
  totalRecords: number
  skipped: boolean
}

export interface CheckpointRunResponse {
  outcome: string
  checkpoint: CheckpointDto
}

export interface MockOutageResponse {
  sourceId: string
  seconds: number
  outageUntilUtc: string | null
}

/** RFC 9457 problem details（ASP.NET Core ProblemDetails / ValidationProblemDetails）。 */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}

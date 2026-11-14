import { describeApiError, type ApiError } from '../api/client'
import type { SourceDto } from '../api/types'
import { useNow } from '../hooks/useNow'
import type { ConnectionState } from '../lib/dataHubState'
import { formatDuration, formatUtcTime } from '../lib/format'
import { statusInfo } from '../lib/status'

interface BannersProps {
  connection: ConnectionState
  disconnectedSinceMs: number | null
  lastRefreshMs: number | null
  pollIntervalMs: number
  /** backend 有回應但 request 失敗（validation、404、source failed…）。 */
  apiError: ApiError | null
  sources: readonly SourceDto[] | null
}

/** 只有呢個小元件每秒重繪；用 aria-hidden 避免 screen reader 每秒重讀。 */
function DownFor({ sinceMs }: { sinceMs: number }) {
  const nowMs = useNow(1000)
  return (
    <span className="dim" aria-hidden="true">
      Down for {formatDuration(nowMs - sinceMs)}
    </span>
  )
}

export function Banners({
  connection,
  disconnectedSinceMs,
  lastRefreshMs,
  pollIntervalMs,
  apiError,
  sources,
}: BannersProps) {
  const notLive = (sources ?? []).filter((source) => source.status !== 'Live')

  return (
    <div className="banners">
      {connection === 'disconnected' ? (
        <div className="banner banner-down" role="alert">
          <strong>BACKEND UNREACHABLE</strong>
          <span>
            {lastRefreshMs !== null
              ? `Showing the last data received at ${formatUtcTime(new Date(lastRefreshMs).toISOString())} UTC; `
              : 'No data received yet; '}
            source statuses cannot be confirmed. Retrying every {pollIntervalMs} ms.
          </span>
          {disconnectedSinceMs !== null ? <DownFor sinceMs={disconnectedSinceMs} /> : null}
        </div>
      ) : null}

      {apiError ? (
        <div className="banner banner-warn" role="alert">
          <strong>API ERROR</strong>
          <span>{describeApiError(apiError)}</span>
        </div>
      ) : null}

      {connection === 'connected' && notLive.length > 0 ? (
        <output className="banner banner-info">
          <strong>
            {notLive.length} OF {sources?.length ?? 0} PRICE SOURCES NOT LIVE
          </strong>
          <span>
            {notLive.map((source) => `${source.sourceId} ${statusInfo(source.status).label}`).join(' · ')} — values from
            these sources may be outdated.
          </span>
        </output>
      ) : null}
    </div>
  )
}

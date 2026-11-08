/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** UI polling 間隔（毫秒），預設 1000，限制喺 250–60000。 */
  readonly VITE_POLL_INTERVAL_MS?: string
  /** 留空 = 同源 /api（Vite proxy 或 nginx reverse proxy）；填 backend URL 則 browser 直接呼叫（需要 CORS）。 */
  readonly VITE_API_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

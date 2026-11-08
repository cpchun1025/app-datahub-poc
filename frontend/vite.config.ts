import react from '@vitejs/plugin-react'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

// 本機開發：browser 一律打同源 /api，由 Vite proxy 轉發去 backend，所以唔需要 CORS。
// 如果想 browser 直接打 backend，可以設定 VITE_API_BASE_URL（backend Development 設定已允許 http://localhost:5173）。
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.DATAHUB_DEV_PROXY_TARGET || 'http://localhost:5080'
  const proxy = { '/api': { target, changeOrigin: false } }

  return {
    plugins: [react()],
    server: { port: 5173, strictPort: true, proxy },
    preview: { port: 4173, strictPort: true, proxy },
    test: {
      environment: 'node',
      include: ['src/**/*.test.{ts,tsx}'],
    },
  }
})

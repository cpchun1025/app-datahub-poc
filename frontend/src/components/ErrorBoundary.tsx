import { Component, type ErrorInfo, type ReactNode } from 'react'

interface ErrorBoundaryProps {
  children: ReactNode
}

interface ErrorBoundaryState {
  error: Error | null
}

/**
 * 最後防線：render 期間出現未預期錯誤時，唔好成頁變白，而係顯示明確訊息同重新載入掣。
 * React 只支援用 class component 做 error boundary。
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Data Hub UI stopped rendering', error, info.componentStack)
  }

  render() {
    const { error } = this.state
    if (error === null) {
      return this.props.children
    }

    return (
      <main className="fatal" role="alert">
        <h1>UI ERROR</h1>
        <p>The dashboard hit an unexpected error and stopped rendering. The backend and its data are not affected.</p>
        <pre>{error.message}</pre>
        <button type="button" className="btn" onClick={() => window.location.reload()}>
          Reload
        </button>
      </main>
    )
  }
}

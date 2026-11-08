export interface PollerOptions {
  intervalMs: number
  /** 每一輪要做嘅工作。收到嘅 signal 會喺 stop() 時被 abort；run 要自己處理錯誤，拋出嘅錯誤唔會中斷 polling。 */
  run: (signal: AbortSignal) => Promise<void>
  /** run 拋出（而且唔係因為 stop() 取消）嘅錯誤。 */
  onError?: (error: unknown) => void
}

export interface Poller {
  start(): void
  stop(): void
}

/**
 * 循序 polling：一輪完成之後先排下一輪（setTimeout chain），所以慢 request 唔會令請求無限制重疊。
 * 一個 poller 只可以 start 一次；stop 之後會取消進行中嘅 request 同 timer。要換參數就建立新 poller。
 */
export function createPoller({ intervalMs, run, onError }: PollerOptions): Poller {
  let state: 'idle' | 'running' | 'stopped' = 'idle'
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined

  async function cycle(): Promise<void> {
    timer = undefined
    if (state !== 'running') {
      return
    }

    const current = new AbortController()
    controller = current
    try {
      await run(current.signal)
    } catch (error) {
      if (!current.signal.aborted) {
        onError?.(error)
      }
    } finally {
      if (controller === current) {
        controller = undefined
      }
    }

    if (state === 'running') {
      timer = setTimeout(() => {
        void cycle()
      }, intervalMs)
    }
  }

  return {
    start() {
      if (state !== 'idle') {
        return
      }

      state = 'running'
      void cycle()
    },
    stop() {
      state = 'stopped'
      if (timer !== undefined) {
        clearTimeout(timer)
        timer = undefined
      }

      controller?.abort()
    },
  }
}

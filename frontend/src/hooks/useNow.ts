import { useEffect, useState } from 'react'

/** 每 intervalMs 更新一次嘅 Date.now()，供 UTC clock 同「已斷線 N 秒」等顯示使用。 */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])

  return now
}

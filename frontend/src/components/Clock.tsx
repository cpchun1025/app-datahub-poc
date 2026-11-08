import { useNow } from '../hooks/useNow'
import { formatClock } from '../lib/format'

export function Clock() {
  const nowMs = useNow(1000)
  return (
    <time className="clock" dateTime={new Date(nowMs).toISOString()}>
      {formatClock(nowMs)} UTC
    </time>
  )
}

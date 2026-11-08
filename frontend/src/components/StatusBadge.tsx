import type { StatusInfo } from '../lib/status'
import { StatusGlyph } from './StatusGlyph'

export function StatusBadge({ info, title }: { info: StatusInfo; title?: string }) {
  return (
    <span className={`status status-${info.tone}`} title={title ?? info.hint}>
      <StatusGlyph tone={info.tone} />
      <span>{info.label}</span>
    </span>
  )
}

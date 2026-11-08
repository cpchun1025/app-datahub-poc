import type { ReactNode } from 'react'
import type { StatusTone } from '../lib/status'

// 每個狀態用唔同形狀：實心圓＝Live、虛線圓＝Init、半圓＝Recovering、三角＝Stale、叉＝Disconnected。
const SHAPES: Record<StatusTone, ReactNode> = {
  live: <circle cx="6" cy="6" r="4.2" fill="currentColor" />,
  init: <circle cx="6" cy="6" r="3.6" fill="none" stroke="currentColor" strokeWidth="1.5" strokeDasharray="2 1.6" />,
  recovering: (
    <>
      <circle cx="6" cy="6" r="4.2" fill="none" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6 1.8 A4.2 4.2 0 0 0 6 10.2 Z" fill="currentColor" />
    </>
  ),
  stale: <path d="M6 1.4 L11 10.4 H1 Z" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="miter" />,
  down: <path d="M2 2 L10 10 M10 2 L2 10" fill="none" stroke="currentColor" strokeWidth="1.9" />,
}

export function StatusGlyph({ tone }: { tone: StatusTone }) {
  return (
    <svg className="glyph" viewBox="0 0 12 12" width="12" height="12" aria-hidden="true" focusable="false">
      {SHAPES[tone]}
    </svg>
  )
}

import type { ReactNode } from 'react'
import type { PriceDto } from '../api/types'
import { formatPrice, formatUtcDateTimeMs } from '../lib/format'
import { statusInfo } from '../lib/status'

interface PriceDetailProps {
  row: PriceDto | null
  decimals: number | undefined
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <span className="detail-field">
      <span className="k">{label}</span> <span className="v">{children}</span>
    </span>
  )
}

/** 已選取 row 嘅完整資料（原始 JSON 價格、毫秒時間）。冇選取時保留一行提示，避免版面跳動。 */
export function PriceDetail({ row, decimals }: PriceDetailProps) {
  if (row === null) {
    return (
      <div className="detail detail-empty">
        Select a row (click, or ↑ ↓ PgUp PgDn Home End; Esc clears) to see its full record.
      </div>
    )
  }

  return (
    <div className="detail">
      <Field label="SELECTED">
        <strong className="amber">
          {row.sourceId} / {row.instrumentId}
        </strong>
      </Field>
      <Field label="PRICE">
        {formatPrice(row.price, decimals)} {row.currency}
        <span className="dim"> (raw {String(row.price)})</span>
      </Field>
      <Field label="SOURCE TIME">{formatUtcDateTimeMs(row.sourceUpdatedAtUtc)} UTC</Field>
      <Field label="RECEIVED">{formatUtcDateTimeMs(row.receivedAtUtc)} UTC</Field>
      <Field label="REV">{row.sourceRevision}</Field>
      <Field label="STATUS">{statusInfo(row.sourceStatus).label}</Field>
    </div>
  )
}

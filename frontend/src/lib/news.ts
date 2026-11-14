import type { NewsItem } from '../components/NewsPanel'

export function safeNewsUrl(value: string): string | null {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}

export function publishedLabel(item: Pick<NewsItem, 'publishedAtUtc' | 'publishedDate'>): string {
  return item.publishedAtUtc ?? (item.publishedDate ? `${item.publishedDate} (date only)` : 'Unknown')
}

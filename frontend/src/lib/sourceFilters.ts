import type { SourceDto } from '../api/types'

export function filterSources(
  sources: readonly SourceDto[],
  search: string,
  kind: string,
  status: string,
): SourceDto[] {
  const needle = search.trim().toLocaleLowerCase()
  return sources.filter((source) =>
    (!needle || source.sourceId.toLocaleLowerCase().includes(needle)
      || source.displayName.toLocaleLowerCase().includes(needle))
    && (!kind || source.kind === kind)
    && (!status || source.status === status),
  )
}

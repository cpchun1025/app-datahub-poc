import { describe, expect, it } from 'vitest'
import type { CheckpointDto } from '../api/types'
import { checkpointLoadSummary, checkpointView } from './checkpointView'

function checkpoint(overrides: Partial<CheckpointDto> = {}): CheckpointDto {
  return {
    enabled: true,
    loadedFrom: null,
    loadedSavedAtUtc: null,
    loadedRecordCount: null,
    loadWarnings: [],
    saveInProgress: false,
    lastAttemptAtUtc: null,
    lastSuccessAtUtc: null,
    lastSuccessRecordCount: null,
    lastSuccessBytes: null,
    lastSuccessDurationMs: null,
    lastErrorAtUtc: null,
    lastError: null,
    consecutiveFailures: 0,
    ...overrides,
  }
}

describe('checkpointView', () => {
  it('reports unknown before the first health response', () => {
    expect(checkpointView(null).label).toBe('UNKNOWN')
  })

  it('reports a disabled checkpoint without implying it is healthy', () => {
    const view = checkpointView(checkpoint({ enabled: false }))
    expect(view).toMatchObject({ tone: 'dim', label: 'DISABLED' })
  })

  it('reports none yet when nothing has been saved', () => {
    expect(checkpointView(checkpoint())).toMatchObject({ tone: 'dim', label: 'NONE YET' })
  })

  it('summarises the last successful save', () => {
    const view = checkpointView(
      checkpoint({
        lastSuccessAtUtc: '2025-01-02T03:04:05Z',
        lastSuccessRecordCount: 4300,
        lastSuccessBytes: 2048,
        lastSuccessDurationMs: 35,
      }),
    )

    expect(view.tone).toBe('ok')
    expect(view.detail).toBe('03:04:05 UTC · 4,300 records · 2.0 KB · 35 ms')
  })

  it('shows the error and keeps the last good save visible when the latest save failed', () => {
    const view = checkpointView(
      checkpoint({
        lastSuccessAtUtc: '2025-01-02T03:04:05Z',
        lastError: 'Disk full',
        lastErrorAtUtc: '2025-01-02T03:05:00Z',
        consecutiveFailures: 3,
      }),
    )

    expect(view.tone).toBe('error')
    expect(view.label).toBe('ERROR ×3')
    expect(view.detail).toBe('Disk full · last OK 03:04:05 UTC')
  })

  it('says so when a failing checkpoint has never saved successfully', () => {
    const view = checkpointView(checkpoint({ lastError: 'Denied', consecutiveFailures: 1 }))
    expect(view.detail).toBe('Denied · never saved OK')
  })

  it('prefers the error over an in-progress save so a failure is never hidden', () => {
    const view = checkpointView(checkpoint({ saveInProgress: true, lastError: 'x', consecutiveFailures: 1 }))
    expect(view.tone).toBe('error')
  })

  it('shows saving when a save is running and nothing has failed', () => {
    expect(checkpointView(checkpoint({ saveInProgress: true }))).toMatchObject({ tone: 'busy', label: 'SAVING…' })
  })
})

describe('checkpointLoadSummary', () => {
  it('returns null when nothing was restored', () => {
    expect(checkpointLoadSummary(null)).toBeNull()
    expect(checkpointLoadSummary(checkpoint())).toBeNull()
  })

  it('names the file the data was restored from and counts warnings', () => {
    const summary = checkpointLoadSummary(
      checkpoint({
        loadedFrom: 'backup',
        loadedSavedAtUtc: '2025-01-02T03:04:05Z',
        loadedRecordCount: 12,
        loadWarnings: ['primary corrupt'],
      }),
    )

    expect(summary).toBe('Restored at startup from the backup file (saved 2025-01-02 03:04:05 UTC, 12 records) · 1 warning(s)')
  })
})

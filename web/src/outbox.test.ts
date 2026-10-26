import { describe, expect, it } from 'vitest'
import { visibleCommands } from './outbox'

const c = (id: number, status: string, createdAt: string) => ({ id, status, createdAt })
const tail = '2026-01-01T12:00:00Z'

describe('visibleCommands', () => {
  it('always shows pending', () => {
    expect(visibleCommands([c(1, 'queued', '2025-01-01T00:00:00Z'), c(2, 'running', '2025-01-01T00:00:00Z')], tail).map(x => x.id)).toEqual([2, 1])
  })
  it('hides old failed, shows new failed', () => {
    const r = visibleCommands([c(1, 'failed', '2026-01-01T11:00:00Z'), c(2, 'failed', '2026-01-01T13:00:00Z')], tail)
    expect(r.map(x => x.id)).toEqual([2])
  })
  it('shows failed when no tail; hides completed; newest first', () => {
    expect(visibleCommands([c(1, 'failed', 'x'), c(3, 'queued', 'x'), c(2, 'done', 'x')], null).map(x => x.id)).toEqual([3, 1])
  })
})

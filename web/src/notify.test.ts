import { describe, expect, it } from 'vitest'
import type { SessionRow } from './api'
import { pickNew } from './notify'

const row = (state: string, lastActivity = 't1', sessionId = 's'): SessionRow =>
  ({ machineId: 1, machine: 'm', runtime: 'claude', sessionId, cwd: '/x', title: null, state, lastActivity, pendingCommands: 0 })

describe('pickNew', () => {
  it('fires first needsInput, not again for same lastActivity', () => {
    const a = pickNew([row('needsInput')], {})
    expect(a.fire).toHaveLength(1)
    expect(pickNew([row('needsInput')], a.seen).fire).toHaveLength(0)
  })
  it('fires again on new lastActivity', () => {
    const a = pickNew([row('needsInput')], {})
    expect(pickNew([row('needsInput', 't2')], a.seen).fire).toHaveLength(1)
  })
  it('fires after leaving needsInput and coming back', () => {
    const a = pickNew([row('needsInput')], {})
    const b = pickNew([row('working')], a.seen)
    expect(b.seen).toEqual({})
    expect(pickNew([row('needsInput')], b.seen).fire).toHaveLength(1)
  })
  it('never fires for other states', () => {
    for (const s of ['working', 'stalled', 'done']) expect(pickNew([row(s)], {}).fire).toHaveLength(0)
  })
  it('encodes session id in key', () => {
    expect(Object.keys(pickNew([row('needsInput', 't', 'a/b')], {}).seen)).toEqual(['1/a%2Fb'])
  })
})

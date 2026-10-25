import { expect, it } from 'vitest'
import { guard, parseRoute } from './route'

it('malformed hash falls back to list', () => {
  expect(parseRoute('#/s/1/%E0')).toEqual({ name: 'list' })
})
it('parses session route', () => {
  expect(parseRoute('#/s/3/abc')).toEqual({ name: 'session', machineId: 3, sessionId: 'abc' })
})
it('guard ignores calls while pending', async () => {
  let n = 0
  const g = guard(async () => { n++; await new Promise(r => setTimeout(r, 5)) })
  await Promise.all([g(), g()])
  expect(n).toBe(1)
  await g()
  expect(n).toBe(2)
})

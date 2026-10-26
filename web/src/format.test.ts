import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { ago, agent, commandLabel, isOffline, matches, sessionLabel, shortOs, summary, splitAction } from './format'

const now = new Date('2026-10-25T10:05:00Z')
describe('ago', () => {
  it('minutes', () => expect(ago('2026-10-25T10:00:00Z', now)).toBe('5m ago'))
  it('just now', () => expect(ago('2026-10-25T10:04:30Z', now)).toBe('just now'))
  it('days', () => expect(ago('2026-10-24T08:05:00Z', now)).toBe('1d ago'))
  it('hours', () => expect(ago('2026-10-25T07:05:00Z', now)).toBe('3h ago'))
})

function files(d: string): string[] {
  return readdirSync(d).flatMap(f => (statSync(join(d, f)).isDirectory() ? files(join(d, f)) : [join(d, f)]))
}
it('no v-html in src', () => {
  for (const f of files(__dirname).filter(f => !f.endsWith('format.test.ts')))
    expect(readFileSync(f, 'utf8'), f).not.toMatch(/v-html|innerHTML/)
})

describe('commandLabel', () => {
  const n = new Date('2026-10-25T10:10:00Z')
  const run = { status: 'running', startedAt: '2026-10-25T10:00:00Z', heartbeatAt: '2026-10-25T10:09:50Z' }
  it('queued', () => expect(commandLabel({ status: 'queued' }, '', n, false)).toEqual({ text: 'waiting for runner', tone: 'normal' }))
  it('delivered slot', () => expect(commandLabel({ status: 'delivered' }, '', n, false).text).toBe('waiting for a free slot'))
  it('delivered behind', () => expect(commandLabel({ status: 'delivered' }, '', n, true).text).toBe('queued behind previous message'))
  it('not responding', () => expect(commandLabel({ ...run, heartbeatAt: '2026-10-25T10:08:00Z' }, '2026-10-25T10:09:59Z', n, false)).toEqual({ text: 'runner not responding', tone: 'bad' }))
  it('stuck', () => expect(commandLabel(run, '2026-10-25T10:03:00Z', n, false)).toEqual({ text: 'quiet for 7m, may be stuck', tone: 'warn' }))
  it('busy', () => expect(commandLabel(run, '2026-10-25T10:09:30Z', n, false)).toEqual({ text: 'running 10m · last activity 30s ago', tone: 'busy' }))
  it('done/failed', () => {
    expect(commandLabel({ status: 'done' }, '', n, false)).toEqual({ text: 'done', tone: 'normal' })
    expect(commandLabel({ status: 'failed' }, '', n, false).tone).toBe('bad')
  })
})

it('isOffline', () => {
  expect(isOffline(null, now)).toBe(true)
  expect(isOffline('2026-10-25T10:02:00Z', now)).toBe(true)
  expect(isOffline('2026-10-25T10:04:30Z', now)).toBe(false)
})

it('shortOs', () => {
  expect(shortOs('Microsoft Windows 10.0.26200')).toBe('Windows')
  expect(shortOs('Darwin 23.4.0')).toBe('macOS')
  expect(shortOs('macOS 14.4')).toBe('macOS')
  expect(shortOs('Ubuntu 22.04.3 LTS')).toBe('Ubuntu 22.04')
  expect(shortOs('Linux 5.15.0-generic')).toBe('Linux')
  expect(shortOs(null)).toBe('-')
})

it('agent and splitAction', () => {
  expect(agent('claude')).toEqual({ badge: 'C', name: 'Claude Code' })
  expect(agent('pi').badge).toBe('π')
  expect(agent('zed')).toEqual({ badge: 'Z', name: 'zed' })
  expect(splitAction('Bash: dotnet test: x')).toEqual({ verb: 'Bash', arg: 'dotnet test: x' })
  expect(splitAction('thinking')).toEqual({ verb: '', arg: 'thinking' })
})

it('matches and summary', () => {
  const r = { title: 'Fix Bug', cwd: '/srv/app', machine: 'Box', runtime: 'claude', state: 'working' }
  expect(matches(r, '')).toBe(true)
  expect(matches(r, 'fix')).toBe(true)
  expect(matches(r, 'SRV')).toBe(true)
  expect(matches(r, 'box')).toBe(true)
  expect(matches(r, 'claude code')).toBe(true)
  expect(matches(r, 'nope')).toBe(false)
  expect(matches({ ...r, title: null }, 'fix')).toBe(false)
  expect(summary([r, r, { state: 'done' }])).toEqual({ needsInput: 0, stalled: 0, working: 2, done: 1 })
})

describe('sessionLabel', () => {
  it('prefixes folder to title', () => expect(sessionLabel({ title: 'Fix', cwd: '/a/proj' })).toBe('proj · Fix'))
  it('null title', () => expect(sessionLabel({ title: null, cwd: '/a/proj' })).toBe('proj'))
  it('empty title', () => expect(sessionLabel({ title: '', cwd: '/a/proj/' })).toBe('proj'))
})

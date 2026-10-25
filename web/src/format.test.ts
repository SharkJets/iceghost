import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { ago } from './format'

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

import { describe, expect, it } from 'vitest'
import { fitSize, needsReencode } from './image'

describe('fitSize', () => {
  it('keeps small images', () => expect(fitSize(800, 600)).toEqual({ w: 800, h: 600 }))
  it('scales landscape to 2400', () => expect(fitSize(4800, 2400)).toEqual({ w: 2400, h: 1200 }))
  it('scales portrait to 2400', () => expect(fitSize(1000, 3000)).toEqual({ w: 800, h: 2400 }))
  it('custom max', () => expect(fitSize(400, 200, 100)).toEqual({ w: 100, h: 50 }))
})
describe('needsReencode', () => {
  it('small is kept', () => expect(needsReencode(100_000, 1920, 1080)).toBe(false))
  it('big bytes', () => expect(needsReencode(3 * 1024 * 1024, 1000, 1000)).toBe(true))
  it('big side', () => expect(needsReencode(1000, 2401, 10)).toBe(true))
  it('exactly at limits', () => expect(needsReencode(2.5 * 1024 * 1024, 2400, 2400)).toBe(false))
})

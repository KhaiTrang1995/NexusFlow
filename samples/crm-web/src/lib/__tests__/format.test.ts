import { describe, expect, it } from 'vitest'
import { count, delta, fromNow, fullMoney, money, pct, percent, widthOf } from '../format'

/**
 * How numbers are written.
 *
 * THE TESTS THAT MATTER HERE ARE THE NULL ONES. Every formatter in this file distinguishes "no
 * value" from "zero", because the backend is careful to send null for an empty denominator and a
 * client that rendered it as 0% would throw that care away — a campaign that reached nobody would
 * sort below one that reached a thousand people and converted one.
 */
describe('money', () => {
  it('writes a tile amount compactly', () => {
    expect(money(1_240_000)).toBe('$1.24M')
    expect(money(840_000)).toBe('$840k')
    expect(money(58_500)).toBe('$58.5k')
    expect(money(0)).toBe('$0')
  })

  it('writes a table amount in full', () => {
    expect(fullMoney(184_000)).toBe('$184,000')
  })

  it('keeps a round thousand round', () => {
    // The trim only touches a fractional part. Stripping trailing zeros from "840" would make it
    // "84" — a formatter that divides by ten on exactly the numbers a tile most often shows.
    expect(money(840_000)).toBe('$840k')
    expect(money(200_000)).toBe('$200k')
    expect(money(1_000_000)).toBe('$1M')
  })

  it('says nothing rather than nought when there is no value', () => {
    expect(money(null)).toBe('—')
    expect(fullMoney(undefined)).toBe('—')
  })
})

describe('percent', () => {
  it('turns a fraction into a rate', () => {
    expect(percent(0.58)).toBe('58%')
    expect(percent(0.585, 1)).toBe('58.5%')
  })

  it('is null and not nought when the denominator was empty', () => {
    expect(percent(null)).toBe('—')
    expect(percent(undefined)).toBe('—')
    expect(percent(0)).toBe('0%')
  })

  it('leaves a 0-100 value alone', () => {
    expect(pct(48)).toBe('48%')
    expect(pct(null)).toBe('—')
  })
})

describe('delta', () => {
  it('makes up and down visibly different', () => {
    expect(delta(4, 'pt')).toBe('▲ 4pt')
    expect(delta(-4, 'pt')).toBe('▼ 4pt')
    expect(delta(0)).toBe('— 0')
  })
})

describe('fromNow', () => {
  it('says how long is left', () => {
    expect(fromNow(45)).toBe('in 45m')
    expect(fromNow(180)).toBe('in 3h')
    expect(fromNow(2880)).toBe('in 2d')
  })

  it('says late rather than clamping at zero', () => {
    // The one that matters: "four hours late" and "due now" are the same string to anything that
    // clamps, and they are not the same situation.
    expect(fromNow(-240)).toBe('4h late')
    expect(fromNow(-15)).toBe('15m late')
  })

  it('has no answer when there is no promise', () => {
    expect(fromNow(null)).toBe('—')
  })
})

describe('widthOf', () => {
  it('clamps so a number over target cannot overflow its track', () => {
    expect(widthOf(140, 100)).toBe('100%')
    expect(widthOf(50, 100)).toBe('50%')
    expect(widthOf(-10, 100)).toBe('0%')
  })

  it('is empty rather than infinite when there is no target', () => {
    expect(widthOf(50, 0)).toBe('0%')
  })
})

describe('count', () => {
  it('groups', () => {
    expect(count(12_345)).toBe('12,345')
    expect(count(null)).toBe('—')
  })
})

/**
 * The two units this API uses for a rate, and why they cannot share a formatter.
 *
 * FOUND BY LOOKING AT THE SCREEN, NOT BY A TYPE. `winRate` and `responseRate` are both
 * `number | null` and both mean "a rate"; one is 0–100 and the other is 0–1. Passing the first to
 * `percent` renders a win rate of 10,000% on a board, which is exactly what it did. The compiler
 * cannot see it, so these are here.
 */
describe('the two rate units', () => {
  it('pct is for the 0-100 fields: winRate, attainment, every KPI figure', () => {
    expect(pct(58.3, 1)).toBe('58.3%')
    expect(pct(100)).toBe('100%')
  })

  it('percent is for the 0-1 fields: responseRate and an attributed share', () => {
    expect(percent(0.583, 1)).toBe('58.3%')
    expect(percent(1)).toBe('100%')
  })

  it('and the wrong one is off by two orders of magnitude', () => {
    expect(percent(100)).toBe('10000%')
    expect(pct(1)).toBe('1%')
  })
})

// Fader taper: piecewise-linear dB -> position, so the useful range (-20..0 dB) gets most of the travel.
const POINTS: [number, number][] = [[-89, 0.02], [-60, 0.1], [-40, 0.25], [-20, 0.5], [-10, 0.68], [0, 0.85], [12, 1]]

function raw(db: number): number {
  if (db <= POINTS[0][0]) return POINTS[0][1]
  for (let i = 1; i < POINTS.length; i++) {
    const [d1, p1] = POINTS[i]
    const [d0, p0] = POINTS[i - 1]
    if (db <= d1) return p0 + ((db - d0) / (d1 - d0)) * (p1 - p0)
  }
  return 1
}

/** 0..1 position of a level on a fader whose top is `max` dB. null (-inf) is 0. */
export function toPos(db: number | null, max: number): number {
  if (db === null) return 0
  return raw(db) / raw(max)
}

/** Inverse of toPos, snapped to whole dB; the bottom 1.5% of travel is -inf. */
export function fromPos(pos: number, min: number, max: number): number | null {
  const p = pos * raw(max)
  if (pos < 0.015) return null
  for (let i = 1; i < POINTS.length; i++) {
    const [d1, p1] = POINTS[i]
    const [d0, p0] = POINTS[i - 1]
    if (p <= p1) return clamp(Math.round(d0 + ((p - p0) / (p1 - p0)) * (d1 - d0)), min, max)
  }
  return max
}

export const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))

export function fmtDb(db: number | null): string {
  if (db === null) return '−∞'
  if (db === 0) return '0'
  return (db > 0 ? '+' : '−') + Math.abs(db)
}

/** dB -> 0..1 on a -60..0 dB scale, in 2% steps (~1.2 dB): finer movement is invisible and would only cost repaints. */
export const dbPos = (db: number) => Math.round(clamp((db + 60) / 60, 0, 1) * 50) / 50

/** Meter value from the device (dB x 10). */
export const meterPos = (tenths: number | undefined) => (tenths === undefined || tenths <= -600 ? 0 : dbPos(tenths / 10))

/** Windows / driver linear peak 0..1. */
export const peakPos = (p: number | undefined) => (!p || p <= 0.001 ? 0 : dbPos(20 * Math.log10(p)))

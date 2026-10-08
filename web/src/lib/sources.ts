import { PAIRS, type Mixer } from './model'

/**
 * Mixer sources as the user sees them. A linked pair is one stereo source driving both
 * channels; an unlinked pair splits into two mono sources (with pan).
 */
export interface SourceRow {
  key: string           // name key in profile.names
  fallback: string
  pair: number          // 0..5
  channels: number[]    // mixer channel indexes
  meter?: string[]      // HID meter params
  /** Playback pair index 0..3 (Windows "Playback 1/2".."7/8") for playback sources. */
  playback?: number
}

export function sourceRows(mixer: Mixer): SourceRow[] {
  const out: SourceRow[] = []
  PAIRS.forEach((p, k) => {
    const meter = 'meter' in p ? [...p.meter] : undefined
    const playback = k >= 2 ? k - 2 : undefined
    if (mixer.mixes[0].link[k]) out.push({ key: `pair${k}`, fallback: p.name, pair: k, channels: [2 * k, 2 * k + 1], meter, playback })
    else {
      out.push({ key: `ch${2 * k}`, fallback: p.left, pair: k, channels: [2 * k], meter: meter && [meter[0]], playback })
      out.push({ key: `ch${2 * k + 1}`, fallback: p.right, pair: k, channels: [2 * k + 1], meter: meter && [meter[1]], playback })
    }
  })
  return out
}

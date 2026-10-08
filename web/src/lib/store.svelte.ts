import { connect, send, type Change } from './bridge'
import { MIXES, SOURCE_LABEL, type OutputKey, type Profile, type Source, type Status, type WinTopology } from './model'

export const app = $state({
  profile: null as Profile | null,
  status: { connected: false, controlCenterRunning: false, monitorMixKnob: null } as Status,
  meters: {} as Record<string, number>,
  /** Windows endpoint / session peaks, linear 0..1, keyed by endpoint id or session key. */
  winPeaks: {} as Record<string, number>,
  windows: { endpoints: [], sessions: [] } as WinTopology,
  /** Result of the last "store on device": null while idle. */
  stored: null as boolean | null,
  /** Last result of a slow/fallible host operation (sample rate, Windows layout). */
  result: null as { op: string; ok: boolean; message?: string } | null,
  firmware: null as { state: string; progress: number; message?: string } | null,
  /** TPConsole's own CPU (% of all cores, like Task Manager) and memory, incl. the web view. */
  perf: null as { cpu: number; memMb: number } | null,
})

connect({
  state: (p, s) => { app.profile = p; app.status = s },
  // The host sends only values that changed; merging per key re-renders only what reads them.
  meters: (m, w) => { Object.assign(app.meters, m); Object.assign(app.winPeaks, w) },
  windows: t => { app.windows = t },
  stored: ok => { app.stored = ok },
  result: (op, ok, message) => { app.result = { op, ok, message } },
  firmware: (state, progress, message) => { app.firmware = { state, progress, message } },
  perf: p => { app.perf = p },
})

function getPath(root: any, path: string): unknown {
  let node = root
  for (const p of path.split('.')) { if (node == null) return undefined; node = node[p] }
  return $state.snapshot(node)
}

function setPath(root: any, path: string, value: unknown) {
  const parts = path.split('.')
  let node = root
  for (const p of parts.slice(0, -1)) node = node[p]
  node[parts.at(-1)!] = value
}

// ---- undo / redo: every change made through set() can be taken back -------------------
// View-only settings are not worth undoing.
const NOT_UNDOABLE = /^settings\.(routingZoom|uiScale|hintsSeen|language|theme)$/
type Step = { undo: Change[]; redo: Change[]; at: number; key: string }
const undoStack: Step[] = []
const redoStack: Step[] = []
export const history = $state({ canUndo: false, canRedo: false })
const refresh = () => { history.canUndo = undoStack.length > 0; history.canRedo = redoStack.length > 0 }

function apply(changes: Change[]) {
  for (const c of changes) setPath(app.profile, c.path, c.value)
  send(changes)
}

/** Apply locally (optimistic) and send to the host. Paths are relative to the profile. */
export function set(...changes: Change[]) {
  if (!app.profile) return
  const undoable = changes.filter(c => !NOT_UNDOABLE.test(c.path))
  if (undoable.length) {
    const key = undoable.map(c => c.path).join('|')
    const last = undoStack.at(-1)
    const now = Date.now()
    const before = undoable.map(c => ({ path: c.path, value: getPath(app.profile, c.path) ?? null }))
    // A fader drag is many changes to the same paths: one step.
    if (last && last.key === key && now - last.at < 800) { last.redo = undoable; last.at = now }
    else {
      undoStack.push({ undo: before, redo: undoable, at: now, key })
      if (undoStack.length > 200) undoStack.shift()
    }
    redoStack.length = 0
    refresh()
  }
  apply(changes)
}

export function undo() {
  const s = undoStack.pop()
  if (!s || !app.profile) return
  redoStack.push(s)
  apply(s.undo)
  refresh()
}

export function redo() {
  const s = redoStack.pop()
  if (!s || !app.profile) return
  undoStack.push({ ...s, at: 0 })
  apply(s.redo)
  refresh()
}

export function rename(key: string, value: string) {
  set({ path: `names.${key}`, value: value.trim() || null })
}

export function nameOf(key: string, fallback: string): string {
  return app.profile?.names[key] || fallback
}

export const mixName = (m: number) => nameOf(`mix${m}`, `MIX ${MIXES[m]}`)
export const mixColor = (m: number) => `var(--mix-${MIXES[m].toLowerCase()})`

/** One field of a source's send into MIX m, on each of its channels (a linked pair has two). */
export const setSend = (m: number, channels: number[], field: string, value: unknown) =>
  set(...channels.map(c => ({ path: `mixer.mixes.${m}.channel.${c}.${field}`, value })))

/** Output level: left, plus right when the sides are linked. */
export const setOutLevel = (k: OutputKey, v: number | null, linked = true) =>
  set({ path: `mixer.${k}.levelDbL`, value: v }, ...(linked ? [{ path: `mixer.${k}.levelDbR`, value: v }] : []))

/** Make an output play a source (and reconnect it). */
export const setOutSource = (k: OutputKey, value: Source) =>
  set({ path: `mixer.${k}.source`, value }, { path: `mixer.${k}.muteL`, value: false }, { path: `mixer.${k}.muteR`, value: false })

/** What an output plays: a mix (by its name) or a direct source. */
export const sourceName = (s: Source) => (s.startsWith('Mix') ? mixName('ABCD'.indexOf(s.slice(3))) : SOURCE_LABEL[s])

/** A muted output counts as disconnected (outputs always have a source on the device). */
export function outOff(k: OutputKey) {
  const o = app.profile!.mixer[k]
  return o.muteL && (o.link || o.muteR)
}

/** Disconnect (mute) or reconnect an output, both sides. */
export const muteOut = (k: OutputKey, on: boolean) =>
  set({ path: `mixer.${k}.muteL`, value: on }, { path: `mixer.${k}.muteR`, value: on })

/** Device meter of each mixer channel's source: IN 1, IN 2, Mobile IN L/R, then Playback 1..8.
 *  41..48.01 are the E2x2's playback meters (they follow the Windows stream, not a mix). */
const CHANNEL_METER = ['21.04', '23.04', '22.04', '24.04', '41.01', '42.01', '43.01', '44.01', '45.01', '46.01', '47.01', '48.01']

/** Level of MIX m, left and right, in the device's meter units (dB x 10). The E2x2 does not meter its
 *  mixes, so this adds up the connected sources at their send level and pan (power sum), like the wires. */
export function mixMeters(m: number): (number | undefined)[] {
  const mix = app.profile?.mixer.mixes[m]
  if (!mix) return [undefined, undefined]
  const anySolo = mix.channel.some(c => c.solo)
  const power = [0, 0]
  mix.channel.forEach((c, i) => {
    const src = app.meters[CHANNEL_METER[i]]
    if (c.mute || (anySolo && !c.solo) || c.levelDb === null || src === undefined) return
    const p = 10 ** ((src / 10 + c.levelDb) / 10)
    power[0] += p * ((100 - c.pan) / 100) ** 2
    power[1] += p * (c.pan / 100) ** 2
  })
  return power.map(p => (p > 0 ? Math.round(100 * Math.log10(p)) : undefined))
}

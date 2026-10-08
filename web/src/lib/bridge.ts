// Talks to the WPF host through WebView2. Outside the host (vite dev in a browser) a mock
// device answers instead, so the UI can be designed without touching the hardware.
import type { Profile, Status, WinTopology } from './model'
import mockProfile from './mockProfile.json'

export interface Change { path: string; value: unknown }

type StateMsg = { type: 'state'; profile: Profile; status: Status }
type MetersMsg = { type: 'meters'; m: Record<string, number>; w: Record<string, number> }
type WindowsMsg = { type: 'windows'; topology: WinTopology }
type StoredMsg = { type: 'stored'; ok: boolean }
type ResultMsg = { type: 'result'; op: string; ok: boolean; message?: string }
type FirmwareMsg = { type: 'firmware'; state: string; progress: number; message?: string }
type PerfMsg = { type: 'perf'; cpu: number; memMb: number }
type HostMsg = StateMsg | MetersMsg | WindowsMsg | StoredMsg | ResultMsg | FirmwareMsg | PerfMsg

interface WebView { postMessage(m: unknown): void; addEventListener(t: 'message', f: (e: { data: HostMsg }) => void): void }
const webview: WebView | undefined = (window as any).chrome?.webview

export const isMock = !webview
/** Preview with ?demo (README screenshots): English, no tips, no preview badge. */
export const isDemo = isMock && new URLSearchParams(location.search).has('demo')

export interface Handlers {
  state: (p: Profile, s: Status) => void
  meters: (m: Record<string, number>, w: Record<string, number>) => void
  windows: (t: WinTopology) => void
  stored: (ok: boolean) => void
  result: (op: string, ok: boolean, message?: string) => void
  firmware: (state: string, progress: number, message?: string) => void
  perf: (p: { cpu: number; memMb: number }) => void
}

export function connect(h: Handlers) {
  if (webview) {
    webview.addEventListener('message', e => {
      const msg = e.data
      if (msg.type === 'state') h.state(msg.profile, msg.status)
      else if (msg.type === 'windows') h.windows(msg.topology)
      else if (msg.type === 'stored') h.stored(msg.ok)
      else if (msg.type === 'result') h.result(msg.op, msg.ok, msg.message)
      else if (msg.type === 'firmware') h.firmware(msg.state, msg.progress, msg.message)
      else if (msg.type === 'perf') h.perf(msg)
      else if (msg.type === 'meters') h.meters(msg.m, msg.w ?? {})
    })
    webview.postMessage({ op: 'hello' })
    return
  }
  mock = { ...h, state: (p, st) => { mockLast = p; h.state(p, st) } }
  const profile = structuredClone(mockProfile) as Profile
  if (isDemo) Object.assign(profile.settings, { language: 'en', hintsSeen: true })
  mock.state(profile, MOCK_STATUS)
  mockWindows = h.windows
  pushWindows()
  startMockMeters(h.meters)
  setInterval(() => h.perf({ cpu: 0.4 + Math.random() * 0.2, memMb: 92 }), 2000)
}

const ep = (id: string, name: string, e2x2: string | null, flow: 'render' | 'capture' = 'render', device = 'E2x2 OTG') =>
  ({ id, name, e2x2, flow, device, isDefault: id === 'pb12', isDefaultComm: id === 'pb12', volume: 0.8, muted: false })
const MOCK_WINDOWS: WinTopology = {
  endpoints: [
    ep('pb12', 'Playback 1/2', 'pb0'), ep('pb34', 'Playback 3/4', 'pb1'), ep('pb56', 'Playback 5/6', 'pb2'), ep('pb78', 'Playback 7/8', 'pb3'),
    ep('lb12', 'Loopback 1/2', 'loopback12', 'capture'), ep('an12', 'Analog 1/2', 'analog', 'capture'),
    ep('hdmi', '27M2N5500', null, 'render', 'NVIDIA High Definition Audio'),
  ],
  sessions: [
    { key: 's1', endpoint: 'pb12', pid: 1, name: 'League of Legends', exe: 'LeagueClient', active: true, icon: null },
    { key: 's2', endpoint: 'pb12', pid: 2, name: 'Discord', exe: 'Discord', active: true, icon: null },
    { key: 's3', endpoint: 'pb12', pid: 3, name: 'KakaoTalk', exe: 'KakaoTalk', active: false, icon: null },
    { key: 's4', endpoint: 'pb34', pid: 4, name: 'Spotify', exe: 'Spotify', active: true, icon: null },
    { key: 's5', endpoint: 'hdmi', pid: 5, name: 'Steam', exe: 'steamwebhelper', active: false, icon: null },
    { key: 'r1', endpoint: 'lb12', flow: 'capture', pid: 2, name: 'Discord', exe: 'Discord', active: true, icon: null },
    { key: 'r2', endpoint: 'an12', flow: 'capture', pid: 6, name: 'OBS Studio', exe: 'obs64', active: true, icon: null },
  ],
  asio: { hosts: [{ pid: 9, name: 'Studio Pro', icon: null }] },
  driver: {
    version: '5.74', sampleRate: 48000, sampleRates: [44100, 48000, 88200, 96000, 176400, 192000],
    asio: { bufferSize: 32, safeMode: true, bufferSizes: [8, 16, 32, 64, 128, 256, 512, 1024, 2048] },
    stats: { dropouts: 66530, usbErrors: 469, recentDropouts: 0, recentUsbErrors: 0 },
    events: ['02:41:10 2/2 '],
  },
}

/** Pin an app's Windows output to an endpoint (null = follow the Windows default). */
export function setAppDevice(pid: number, endpoint: string | null) {
  if (webview) { webview.postMessage({ op: 'appDevice', pid, endpoint }); return }
  // Mock: move the app's sessions like Windows would.
  const target = endpoint ?? MOCK_WINDOWS.endpoints.find(e => e.isDefault)!.id
  for (const s of MOCK_WINDOWS.sessions) if (s.pid === pid) { s.endpoint = target; s.pinned = endpoint }
  pushWindows()
}
let mockWindows: Handlers['windows'] | undefined
let mock: Handlers | undefined
let mockLast: Profile | undefined
const pushWindows = () => mockWindows?.(structuredClone(MOCK_WINDOWS))
const pushState = () => mock?.state(structuredClone(mockLast!), MOCK_STATUS)

/** Preset actions; the host replies with a new state. */
export function presetAction(action: 'load' | 'save' | 'rename' | 'delete', name: string, newName?: string, includeWindows = false) {
  if (webview) { webview.postMessage({ op: 'preset', action, name, newName, includeWindows }); return }
  // Mock: mimic Engine's preset methods on a copy of the profile.
  const p = structuredClone(mockLast!)
  const i = p.presets.findIndex(x => x.name === name)
  if (action === 'save') {
    if (i < 0) p.presets.push({ name, mixer: structuredClone(p.mixer) })
    else p.presets[i].mixer = structuredClone(p.mixer)
    p.activePreset = name
  } else if (action === 'load' && i >= 0) { p.mixer = structuredClone(p.presets[i].mixer); p.activePreset = name }
  else if (action === 'rename' && i >= 0 && newName) { p.presets[i].name = newName; if (p.activePreset === name) p.activePreset = newName }
  else if (action === 'delete' && i >= 0) { p.presets.splice(i, 1); if (p.activePreset === name) p.activePreset = null }
  mock?.state(p, MOCK_STATUS)
}

const MOCK_STATUS: Status = {
  connected: true, controlCenterRunning: false, monitorMixKnob: 100,
  device: { autoStandby: true, mobileApp: false, brightness: 2, hardwareVersion: 'V1.01', softwareVersion: 'V1.10' },
  latest: { firmware: 'V1.10' },
  update: { current: '1.0.0', available: '1.0.1', failed: false, installed: true, source: 'github.com/gkfyddl2662/TPConsole', error: null, updating: false },
  virtualRouting: { installed: false, enabled: false, partial: false, loaded: false, version: null, driverVersion: '5.74.0.0', compatible: true, bundledVersion: '5.74.0.0', bundledCompatible: true },
}

/** App volume (0..1) and/or mute for one Windows audio session. */
export function setAppVolume(key: string, volume?: number, mute?: boolean) {
  if (webview) { webview.postMessage({ op: 'appVolume', key, volume, mute }); return }
  const s = MOCK_WINDOWS.sessions.find(x => x.key === key)
  if (s) { if (volume !== undefined) s.volume = volume; if (mute !== undefined) s.muted = mute }
  pushWindows()
}

/** Device-wide setting: sub 0x02 auto standby, 0x03 mobile app, 0x04 brightness (0..2). */
export function setDeviceSetting(sub: 2 | 3 | 4, value: number) {
  if (webview) { webview.postMessage({ op: 'deviceSetting', sub, value }); return }
  const d = MOCK_STATUS.device!
  if (sub === 2) d.autoStandby = !!value
  else if (sub === 3) d.mobileApp = !!value
  else d.brightness = value
  pushState()
}

export function setSampleRate(hz: number) {
  if (webview) { webview.postMessage({ op: 'sampleRate', hz }); return }
  MOCK_WINDOWS.driver!.sampleRate = hz
  pushWindows()
  setTimeout(() => mock?.result('sampleRate', true), 300)
}

/** Windows device volume (0..1) and/or mute. */
export function setEndpointVolume(endpoint: string, volume?: number, mute?: boolean) {
  if (webview) { webview.postMessage({ op: 'endpointVolume', endpoint, volume, mute }); return }
  const e = MOCK_WINDOWS.endpoints.find(x => x.id === endpoint)
  if (e) { if (volume !== undefined) e.volume = volume; if (mute !== undefined) e.muted = mute }
  pushWindows()
}

export function setAsioBuffer(size: number, safeMode: boolean) {
  if (webview) { webview.postMessage({ op: 'asioBuffer', size, safeMode }); return }
  const a = MOCK_WINDOWS.driver!.asio!
  a.bufferSize = size; a.safeMode = safeMode
  pushWindows()
}

/** Problem report (logs + versions in one file): put it on the clipboard as a file, or show it in Explorer. */
export function shareLog(action: 'copy' | 'folder') {
  if (webview) webview.postMessage({ op: 'shareLog', action })
  else mock?.result('shareLog', true)
}

export function resetStats() {
  if (webview) webview.postMessage({ op: 'resetStats' })
}

/** Firmware update: download the official image, or pick a .bin ("pick"). Never sent without the warning dialog. */
export function updateFirmware(source: 'download' | 'pick') {
  if (webview) { webview.postMessage({ op: 'firmware', file: source === 'pick' ? 'pick' : null }); return }
  let p = 0
  const tick = () => { p += 20; mock?.firmware(p < 100 ? 'InProgress' : 'Finished', p); if (p < 100) setTimeout(tick, 300) }
  mock?.firmware('downloading', 0); setTimeout(tick, 300)
}

/** Mini mode: the host shrinks the window (and keeps it on top if set). */
export function setMini(on: boolean) {
  if (webview) webview.postMessage({ op: 'mini', on })
}

/** Virtual routing (Thesycon DSP mixer plugin): install the user's plugin file, switch it on/off, apply devices, remove it. */
export function virtualRouting(action: 'install' | 'enable' | 'disable' | 'remove' | 'apply') {
  if (webview) { webview.postMessage({ op: 'virtualRouting', action }); return }
  const v = (MOCK_STATUS.virtualRouting ??= { installed: false, enabled: false, partial: false, loaded: false, version: null })
  MOCK_STATUS.virtualRoutingBusy = true
  pushState()
  setTimeout(() => {
    if (action === 'install') { v.installed = true; v.version = '5.74.0.0' }
    if (action === 'enable') v.enabled = v.loaded = true
    if (action === 'disable') v.enabled = v.loaded = false
    if (action === 'apply') MOCK_STATUS.virtualPending = false
    if (action === 'remove') Object.assign(v, { installed: false, enabled: false, loaded: false, version: null })
    MOCK_STATUS.virtualRoutingBusy = false
    pushState()
    mock?.result('virtualRouting', true)
  }, 900)
}

/** App update from GitHub releases: install the available one now, or check again. */
export function updateApp(action: 'apply' | 'check') {
  if (webview) webview.postMessage({ op: 'update', action })
}

/** Virtual routing: pick the user's own tusbaudiodsp_mixer.sys (TPConsole doesn't ship it). */
export function choosePluginFile() {
  if (webview) webview.postMessage({ op: 'pluginFile' })
}

export function checkLatest() {
  if (webview) webview.postMessage({ op: 'checkLatest' })
  else setTimeout(pushState, 500)
}

export function setStartWithWindows(on: boolean) {
  if (webview) { webview.postMessage({ op: 'startWithWindows', on }); return }
  MOCK_STATUS.startWithWindows = on
  pushState()
}

/** Renames a Windows sound device (elevated: Windows asks for admin permission). */
export function renameEndpoint(endpoint: string, name: string) {
  if (webview) { webview.postMessage({ op: 'renameEndpoint', endpoint, name }); return }
  const e = MOCK_WINDOWS.endpoints.find(x => x.id === endpoint)
  if (e) e.name = name
  pushWindows()
}

/** Writes the current setup into the device's own memory (HID 11.05). */
export function storeOnDevice() {
  if (webview) webview.postMessage({ op: 'storeOnDevice' })
  else setTimeout(() => mock?.stored(true), 300)
}

/** Makes an endpoint the Windows default (or default communications) device. */
export function setDefaultDevice(endpoint: string, communications: boolean) {
  if (webview) { webview.postMessage({ op: 'setDefault', endpoint, communications }); return }
  for (const e of MOCK_WINDOWS.endpoints) {
    const same = e.flow === MOCK_WINDOWS.endpoints.find(x => x.id === endpoint)!.flow
    if (!same) continue
    if (communications) e.isDefaultComm = e.id === endpoint
    else e.isDefault = e.id === endpoint
  }
  pushWindows()
}

// Changes are coalesced per animation frame so a fader drag sends at most ~60 messages/s.
let pending = new Map<string, unknown>()
let scheduled = false
export function send(changes: Change[]) {
  for (const c of changes) pending.set(c.path, c.value)
  if (!webview) for (const c of changes) mockPatch(c)
  if (scheduled) return
  scheduled = true
  requestAnimationFrame(() => {
    scheduled = false
    const batch = [...pending].map(([path, value]) => ({ path, value }))
    pending = new Map()
    webview?.postMessage({ op: 'patch', changes: batch })
  })
}

// Mock: keep the host's copy current, so later state pushes and presets carry the user's edits.
function mockPatch({ path, value }: Change) {
  if (path.startsWith('virtual')) MOCK_STATUS.virtualPending = true
  const parts = path.split('.')
  let node: any = mockLast
  for (const p of parts.slice(0, -1)) node = node?.[p]
  if (node) node[parts.at(-1)!] = value === undefined ? value : JSON.parse(JSON.stringify(value))
}

function startMockMeters(onMeters: Handlers['meters']) {
  const keys = ['21.04', '22.04', '23.04', '24.04', '31.02', '32.02', '33.02', '34.02',
    '41.01', '42.01', '43.01', '44.01', '45.01', '46.01', '47.01', '48.01', '51.02', '52.02', '5A.02', '5B.02']
  const level: Record<string, number> = Object.fromEntries(keys.map(k => [k, -400]))
  setInterval(() => {
    const t = performance.now() / 1000
    for (const k of keys) {
      const target = k.startsWith('2') ? (k === '21.04' ? -180 + 120 * Math.sin(t * 2.3) : -900)
        : k.startsWith('4') && k >= '45' ? -900
        : -160 + 60 * Math.sin(t * 1.7 + k.charCodeAt(1)) + 40 * Math.random()
      level[k] += (target - level[k]) * 0.35
    }
    const w = (base: number) => Math.max(0, base + 0.15 * Math.sin(t * 3 + base * 9) + 0.05 * Math.random())
    onMeters(Object.fromEntries(keys.map(k => [k, Math.round(level[k])])),
      { pb12: w(0.45), s1: w(0.4), s2: w(0.15), s3: 0, pb34: w(0.3), s4: w(0.3), pb56: 0, pb78: 0, hdmi: 0, s5: 0, 'drv:pb0': w(0.45), 'drv:pb1': w(0.5), r1: w(0.3), r2: w(0.2) })
  }, 33)
}

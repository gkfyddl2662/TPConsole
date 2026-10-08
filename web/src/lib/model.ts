// Mirrors TPConsole.Core.Profile as serialized by Engine.Json (camelCase, enums as strings).

export type Source =
  | 'In1' | 'MobileIn' | 'In2' | 'In4' | 'In12' | 'In34'
  | 'Playback12' | 'Playback34' | 'Playback56' | 'Playback78'
  | 'MixA' | 'MixB' | 'MixC' | 'MixD'

export interface InputChannel {
  gainDb: number
  phantom48V: boolean
  instrument: boolean
  monitor: boolean
  mute: boolean
  solo: boolean
  invert: boolean
}

export interface Output {
  source: Source
  levelDbL: number | null
  levelDbR: number | null
  link: boolean
  muteL: boolean
  muteR: boolean
  invert: boolean
  headphone: boolean
  line: boolean
  aux: boolean
}

export interface MixChannel {
  levelDb: number | null
  pan: number
  mute: boolean
  solo: boolean
  invert: boolean
}

export interface Mix {
  channel: MixChannel[]
  link: boolean[]
}

export interface Mixer {
  inputs: InputChannel[]
  phoneGainHigh1: boolean
  phoneGainHigh2: boolean
  out12: Output
  mobileOut: Output
  loopback12: Output
  loopback34: Output
  loopback56: Output
  spdif: Output
  mixes: Mix[]
}

export interface Preset { name: string; mixer: Mixer; windows?: unknown }

export interface VirtualRoutingState {
  installed: boolean
  enabled: boolean
  /** Only one of the two switches (filter / EnablePlugin) is set. */
  partial: boolean
  /** Removed; the still-loaded driver goes away when the E2x2 re-plugs or Windows restarts. */
  removing?: boolean
  loaded: boolean
  version: string | null
  /** The E2x2's USB audio driver version (the plugin must come from the same release). */
  driverVersion?: string | null
  compatible?: boolean
  /** The plugin file the user provided (not shipped with TPConsole), and whether it matches the installed driver. */
  bundledVersion?: string | null
  bundledCompatible?: boolean
}

export interface VirtualDevice { id: number; name: string; kind: 'playback' | 'recording' }
export interface VirtualRoute { from: string; to: string }

export interface PresetRule { app: string; preset: string; revert: boolean }

export interface Hotkey { action: string; ctrl: boolean; alt: boolean; shift: boolean; win: boolean; key: number }

export interface AppSettings {
  language: 'auto' | 'en' | 'ko'
  theme?: 'auto' | 'dark' | 'light'
  hintsSeen: boolean
  syncWindowsNames: boolean
  uiScale: number
  routingZoom: number
  closeToTray: boolean
  autoStoreOnDevice: boolean
  hotkeys: Hotkey[]
  hideServiceSessions?: boolean
  /** Hide apps that have audio open but are silent (default off: the list matches the volume mixer). */
  hideSilentApps?: boolean
  presetRules?: PresetRule[]
  /** Send row order per hardware mix ("0".."3" -> row keys). */
  mixOrder?: Record<string, string[]>
  miniOnTop?: boolean
  /** Meter / wire refresh in Hz; 0 = off. */
  meterRate?: number
  /** Install a newer GitHub release and restart (default on). */
  autoUpdate?: boolean
}

export interface Profile {
  settings: AppSettings
  presets: Preset[]
  activePreset: string | null
  mixer: Mixer
  names: Record<string, string>
  /** ASIO host process name -> playback pair 0..3 (auto-detected or dragged; ASIO is invisible to Windows).
   *  -1 = cleared by the user, don't auto-detect. */
  asioRoutes: Record<string, number>
  /** Extra Windows devices on the driver's virtual channels (DSP mixer plugin). */
  virtualDevices?: VirtualDevice[]
  /** Stereo routes inside the plugin; see VirtualRoute in Engine.cs for the endpoint names. */
  virtualRoutes?: VirtualRoute[]
  virtualApplied?: string | null
}

/** Device-wide settings and versions reported by the E2x2 (null until reported). */
export interface DeviceInfo {
  autoStandby: boolean | null
  mobileApp: boolean | null
  brightness: number | null
  hardwareVersion: string | null
  softwareVersion: string | null
}

export interface Status {
  connected: boolean
  controlCenterRunning: boolean
  monitorMixKnob: number | null
  device?: DeviceInfo
  /** From Topping's public update manifest; absent when offline. */
  latest?: { firmware?: string } | null
  startWithWindows?: boolean
  controlCenterAutostart?: boolean
  storedUpToDate?: boolean
  /** Thesycon DSP mixer plugin (virtual routing). */
  virtualRouting?: VirtualRoutingState | null
  virtualRoutingBusy?: boolean
  /** Virtual devices / routes changed since they were last written to the driver. */
  virtualPending?: boolean
  /** App auto-update: versions are x.y.z; installed = running from Program Files (dev builds never update). */
  update?: { current: string; available: string | null; failed: boolean; installed: boolean; source: string; error: string | null; updating: boolean } | null
}

export const MIXES = ['A', 'B', 'C', 'D'] as const

export type OutputKey = 'out12' | 'mobileOut' | 'loopback12' | 'loopback34' | 'loopback56' | 'spdif'

export interface OutputDef {
  key: OutputKey
  name: string
  hint: string
  /** Meter params [L, R]; not every output reports one. */
  meter?: [string, string]
  jacks?: ('headphone' | 'line' | 'aux')[]
  phoneGain?: 'phoneGainHigh1' | 'phoneGainHigh2'
}

export const OUTPUTS: OutputDef[] = [
  { key: 'out12', name: 'OUT 1+2', hint: 'Headphones · line', meter: ['31.02', '32.02'], jacks: ['headphone', 'line', 'aux'], phoneGain: 'phoneGainHigh1' },
  { key: 'mobileOut', name: 'Mobile OUT', hint: 'USB-C to phone', meter: ['33.02', '34.02'], jacks: ['headphone', 'line'], phoneGain: 'phoneGainHigh2' },
  { key: 'loopback12', name: 'Loopback 1+2', hint: 'Recording input', meter: ['51.02', '52.02'] },
  { key: 'loopback34', name: 'Loopback 3+4', hint: 'Recording input', meter: ['53.02', '54.02'] },
  { key: 'loopback56', name: 'Loopback 5+6', hint: 'Recording input', meter: ['55.02', '56.02'] },
  { key: 'spdif', name: 'S/PDIF', hint: 'Optical out', meter: ['5A.02', '5B.02'] },
]

/** Mixer channel pairs (6) -> default names. Channel index = 2*pair (+1 for right). */
export const PAIRS = [
  { name: 'Mic', left: 'IN 1', right: 'IN 2', meter: ['21.04', '23.04'] },
  { name: 'Mobile IN', left: 'Mobile L', right: 'Mobile R', meter: ['22.04', '24.04'] },
  { name: 'Desktop', left: 'PB 1', right: 'PB 2', sub: 'Playback 1/2' },
  { name: 'DAW', left: 'PB 3', right: 'PB 4', sub: 'Playback 3/4' },
  { name: 'Playback 5/6', left: 'PB 5', right: 'PB 6', sub: 'Playback 5/6' },
  { name: 'Playback 7/8', left: 'PB 7', right: 'PB 8', sub: 'Playback 7/8' },
] as const

export const SOURCE_LABEL: Record<Source, string> = {
  In1: 'IN 1', In2: 'IN 2', In4: 'IN 4', MobileIn: 'Mobile IN', In12: 'IN 1+2', In34: 'IN 3+4',
  Playback12: 'Playback 1/2', Playback34: 'Playback 3/4', Playback56: 'Playback 5/6', Playback78: 'Playback 7/8',
  MixA: 'MIX A', MixB: 'MIX B', MixC: 'MIX C', MixD: 'MIX D',
}

export const DIRECT_SOURCES: Source[] = ['In12', 'In1', 'In2', 'MobileIn', 'Playback12', 'Playback34', 'Playback56', 'Playback78']

// Windows side (host WindowsAudio.cs).
export interface WinEndpoint {
  id: string
  flow: 'render' | 'capture'
  name: string
  device: string
  isDefault: boolean
  isDefaultComm: boolean
  /** Windows device volume 0..1 and mute. */
  volume?: number
  muted?: boolean
  /** pb0..pb3 = Playback 1/2..7/8, loopback12/34/56, analog, mobileIn */
  e2x2: string | null
}

export interface WinSession {
  key: string
  endpoint: string
  flow?: 'render' | 'capture'
  pid: number
  name: string
  /** Process name without .exe (what software mixes and presets match on). */
  exe?: string | null
  active: boolean
  icon: string | null
  /** System sounds session: always follows the Windows default device. */
  system?: boolean
  /** Session owned by a Windows service (svchost), named after the service. */
  service?: boolean
  /** Chromium/Electron audio-service process: the app's web sounds (notifications, media). */
  webAudio?: boolean
  /** Endpoint the app is pinned to (Windows per-app preference); null = Windows default. */
  pinned?: string | null
  /** Session volume 0..1 and mute (the app's slider in the Windows volume mixer). */
  volume?: number
  muted?: boolean
}

export interface AsioHost { pid: number; name: string; icon: string | null }

/** ASIO is in use; hosts = the apps that loaded the driver's ASIO DLL. */
export interface AsioState { hosts: AsioHost[] }

export interface WinTopology {
  endpoints: WinEndpoint[]
  sessions: WinSession[]
  /** Present while an ASIO host has the driver open. */
  asio?: AsioState | null
  /** USB audio driver info (direct IOCTL). */
  driver?: {
    version: string
    sampleRate: number
    sampleRates: number[]
    asio?: { bufferSize: number; safeMode: boolean; bufferSizes: number[] }
    stats?: { dropouts: number; usbErrors: number; recentDropouts: number; recentUsbErrors: number }
    events?: string[]
  } | null
}

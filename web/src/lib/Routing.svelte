<script lang="ts">
  import { tick } from 'svelte'
  import { app, set, nameOf, mixName, sourceName, outOff, muteOut, mixMeters, mixColor, setSend, setOutLevel, setOutSource } from './store.svelte'
  import {
    MIXES, OUTPUTS, PAIRS, SOURCE_LABEL, DIRECT_SOURCES,
    type Source, type OutputKey, type WinSession, type WinEndpoint, type AsioHost, type VirtualDevice, type VirtualRoute,
  } from './model'
  import { sourceRows, type SourceRow } from './sources'
  import { meterPos, peakPos, dbPos, toPos } from './scale'
  import Name from './Name.svelte'
  import Meter from './Meter.svelte'
  import Fader from './Fader.svelte'
  import Icon from './Icon.svelte'
  import SendPopover from './SendPopover.svelte'
  import OutputPopover from './OutputPopover.svelte'
  import ContextMenu, { type Item } from './ContextMenu.svelte'
  import {
    setDefaultDevice, setAppVolume, setAppDevice, setEndpointVolume, renameEndpoint,
    virtualRouting,
  } from './bridge'
  import { ask } from './confirm.svelte'
  import { vrMessage } from './vrMessage'
  import { t } from './i18n.svelte'
  import { hint, hintState } from './hint.svelte'

  // Signal flow, left to right:
  //   apps -> sources (E2x2 playback pairs, inputs; other Windows outputs) -> MIX A..D -> outputs -> recording apps
  // A wire exists only where sound can actually flow; its brightness follows the live level.
  // Ports: a filled dot is where you can drag a wire from; a hollow ring is where one can be dropped.

  const mixer = $derived(app.profile!.mixer)
  const settings = $derived(app.profile!.settings)
  const sync = $derived(settings?.syncWindowsNames ?? true)
  const rows = $derived(sourceRows(mixer))
  const endpoints = $derived(app.windows.endpoints)
  const byRole = (role: string | undefined) => (role ? endpoints.find(e => e.e2x2 === role) : undefined)
  const pairRole = (k: number) => (k >= 2 ? `pb${k - 2}` : k === 0 ? 'analog' : 'mobileIn')

  const hideServices = $derived(settings?.hideServiceSessions ?? false)
  // Apps with Windows audio open but silent right now: shown dimmed (like the volume mixer lists them), or hidden.
  const hideIdle = $derived(settings?.hideSilentApps ?? false)
  /** One node per app process, like the Windows volume mixer (Discord's two audio processes are two
   *  entries there too). A process keeps a session on every device it ever opened; the node sits where it
   *  plays now, or — when silent — on its per-app device, else the Windows default. Wires go to every
   *  device it is playing into right now (a silent app gets one dashed wire to where it sits). */
  type AppView = WinSession & { members: WinSession[]; playingOn: string[] }
  const appViews = $derived.by((): AppView[] => {
    const defaultOut = endpoints.find(e => e.flow === 'render' && e.isDefault)?.id
    const groups = new Map<string, WinSession[]>()
    for (const s of app.windows.sessions) {
      if (s.flow === 'capture' || !(s.active || s.pid !== 0) || (hideServices && s.service)) continue
      const k = s.system ? 'system' : `pid${s.pid}`
      const g = groups.get(k)
      if (g) g.push(s); else groups.set(k, [s])
    }
    return [...groups].map(([k, members]) => {
      const pinned = members.find(m => m.pinned)?.pinned ?? null
      const playingOn = [...new Set(members.filter(m => m.active).map(m => m.endpoint))]
      // Silent with a per-app device: there, even if its old session sits elsewhere (Windows moves a stream
      // only when it plays again). Silent without one: where its session is (the default if it has one there).
      const wanted = members[0].system ? defaultOut : pinned ?? defaultOut
      const home = playingOn.includes(wanted ?? '') ? wanted! : playingOn[0]
        ?? (pinned && !members[0].system ? pinned : members.some(m => m.endpoint === wanted) ? wanted! : members[0].endpoint)
      const rep = members.find(m => m.endpoint === home && m.active) ?? members.find(m => m.endpoint === home) ?? members[0]
      return { ...rep, key: `g:${k}`, endpoint: home, pinned, active: playingOn.length > 0, members, playingOn }
    }).filter(v => !(hideIdle && !v.active))
  })
  /** Why an app plays on this device, from what Windows reports (it does not say which role a stream
   *  was opened for): its per-app device, the default device, the communications default, or the app's
   *  own setting. */
  // The per-app device (set here or in Windows) covers an app's ordinary sound, not its communications
  // streams, and Windows keeps one per program. Where both could explain a device, both are named.
  // "comm" and "app" streams don't follow the per-app device, so they can't be moved from here: a comm
  // stream moves with the Windows communications device, an app's own choice only inside the app.
  type Reason = { label: string; hint: string; kind: 'pin' | 'comm' | 'default' | 'app' }
  function appReason(s: WinSession): Reason {
    const ep = endpoints.find(e => e.id === s.endpoint)
    const pinned = !!s.pinned && s.pinned === s.endpoint, comm = !!ep?.isDefaultComm
    if (pinned && !s.active) return { kind: 'pin', label: endpointLabel(s.pinned!), hint: t('Set for this app in Windows; it plays here from its next sound') }
    if (pinned && comm) return { kind: 'pin', label: `${endpointLabel(s.pinned!)} · ${t('Comm')}`, hint: t('Set for this app in Windows, and the Windows communications device') }
    if (pinned) return { kind: 'pin', label: endpointLabel(s.pinned!), hint: t('Set for this app in Windows') }
    if (comm) return { kind: 'comm', label: t('Comm'), hint: t('Windows default communications device (voice and calls)') }
    if (ep?.isDefault && !s.pinned) return { kind: 'default', label: t('Default'), hint: t('Follows the Windows default device') }
    return { kind: 'app', label: t('Set in app'), hint: t('Chosen in the app’s own settings, not by Windows') }
  }
  /** Which audio of a Chromium/Electron program a node is, when the program plays from both kinds of
   *  process: web sounds from its audio-service process, its own (native) audio from another. Generic
   *  names, except where the split is known (Discord: alerts vs. its voice engine). */
  const KNOWN_KINDS: Record<string, [string, string]> = { discord: ['Alerts', 'Voice'] }
  function procKind(s: WinSession): string | undefined {
    const same = appViews.filter(v => v.exe && v.exe === s.exe)
    if (!same.some(v => v.webAudio) || !same.some(v => !v.webAudio)) return undefined
    const [web, own] = KNOWN_KINDS[s.exe!.toLowerCase()] ?? ['Web sounds', 'App audio']
    return t(s.webAudio ? web : own)
  }
  const movable = (s: WinSession) => !s.system && ['pin', 'default'].includes(appReason(s).kind)
  const renderSessions = (ep: WinEndpoint | undefined) =>
    ep
      ? appViews.filter(v => v.endpoint === ep.id).sort((a, b) => Number(b.active) - Number(a.active) || a.name.localeCompare(b.name))
      : []
  const viewOf = (node: string) => appViews.find(v => `app:${v.key}` === node)
  const membersOf = (s: WinSession) => (s as AppView).members ?? [s]
  const appPeak = (s: WinSession) => Math.max(0, ...membersOf(s).map(m => peakPos(app.winPeaks[m.key])))
  /** Volume / mute for the whole app (every session), like the Windows volume mixer slider. */
  const setAppLevel = (s: WinSession, volume?: number, mute?: boolean) => membersOf(s).forEach(m => setAppVolume(m.key, volume, mute))
  /** Output device for the whole app (each of its processes). */
  const moveApp = (s: WinSession, endpoint: string | null) =>
    [...new Set(membersOf(s).map(m => m.pid))].forEach(pid => setAppDevice(pid, endpoint))

  // ---- ASIO hosts (invisible to Windows): placed by auto-detection or the menu -------------
  const asio = $derived(app.windows.asio ?? null)
  const asioPair = (name: string): number | undefined => {
    const pb = app.profile!.asioRoutes?.[name]
    return pb === undefined || pb < 0 ? undefined : pb
  }
  const asioUnassigned = $derived((asio?.hosts ?? []).filter(h => asioPair(h.name) === undefined))

  // ---- one node per source pair; an unlinked pair shows two channel rows inside it ----------
  type PairView = { k: number; rows: SourceRow[]; role: string; ep?: WinEndpoint; apps: WinSession[]; asio: AsioHost[] }
  const pairs = $derived(
    PAIRS.map((_, k): PairView => {
      const role = pairRole(k)
      const ep = byRole(role)
      return {
        k, role, ep,
        rows: rows.filter(r => r.pair === k),
        apps: k >= 2 ? renderSessions(ep) : [],
        asio: k >= 2 ? (asio?.hosts ?? []).filter(h => asioPair(h.name) === k - 2) : [],
      }
    }),
  )
  // Other Windows outputs (monitors, other interfaces): apps there are shown too, and can be moved.
  const others = $derived(
    endpoints.filter(e => e.flow === 'render' && !e.e2x2).map(ep => ({ ep, apps: renderSessions(ep) })),
  )

  /** Apps recording from a Windows capture endpoint (by E2x2 role). */
  function recorders(role: string): WinSession[] {
    const ep = byRole(role)
    if (!ep) return []
    return app.windows.sessions
      .filter(s => s.flow === 'capture' && s.endpoint === ep.id && !s.system && s.pid !== 0 && !(hideServices && s.service) && !(hideIdle && !s.active))
      .sort((a, b) => Number(!!a.service) - Number(!!b.service) || Number(b.active) - Number(a.active) || a.name.localeCompare(b.name))
  }

  // ---- names: E2x2 pairs share their name with the Windows device when sync is on ---------
  function pairName(p: PairView) {
    return sync && p.ep ? p.ep.name : nameOf(`pair${p.k}`, PAIRS[p.k].name)
  }
  function renamePair(p: PairView, name: string) {
    if (sync && p.ep && name && name !== p.ep.name) renameEndpoint(p.ep.id, name)
  }
  function outName(key: OutputKey, fallback: string) {
    const ep = key.startsWith('loopback') ? byRole(key) : undefined
    return sync && ep ? ep.name : nameOf(`out:${key}`, fallback)
  }
  /** Label of one channel row inside a split pair. */
  const channelLabel = (r: SourceRow) => (r.pair === 0 ? (r.channels[0] === 0 ? 'IN 1' : 'IN 2') : r.channels[0] % 2 === 0 ? 'L' : 'R')
  /** Short label inside a mix: split mic channels are just "IN 1" / "IN 2". */
  const sendName = (r: SourceRow) => (r.pair === 0 && r.channels.length === 1 ? channelLabel(r) : rowName(r))
  const rowName = (r: SourceRow) => {
    const p = pairs[r.pair]
    return r.channels.length > 1 ? pairName(p) : `${pairName(p)} ${channelLabel(r)}`
  }

  // ---- virtual devices (Thesycon mixer plugin): extra Windows playback / recording devices ------
  // Added here like any node; they become Windows devices when applied (the E2x2 restarts once).
  const vr = $derived(app.status.virtualRouting)
  const vReady = $derived(!!(vr?.installed && vr.enabled && vr.loaded))
  // Only while virtual routing is switched on: otherwise the zone, its wires and menu entries are hidden
  // (the devices stay saved in the profile).
  const vOn = $derived(!!(vr?.installed && vr.enabled))
  const vdevs = $derived(vOn ? app.profile!.virtualDevices ?? [] : [])
  const vroutes = $derived(vOn ? app.profile!.virtualRoutes ?? [] : [])
  const vplays = $derived(vdevs.filter(d => d.kind === 'playback'))
  const vrecs = $derived(vdevs.filter(d => d.kind === 'recording'))
  const vName = (d: VirtualDevice) => d.name.trim() || t(d.kind === 'playback' ? 'Virtual playback {n}' : 'Virtual recording {n}', { n: d.id })
  const vEndpoint = (d: VirtualDevice) => byRole(`v:${d.id}`)
  const putV = (devices: VirtualDevice[], routes: VirtualRoute[] = vroutes) =>
    set({ path: 'virtualDevices', value: devices }, { path: 'virtualRoutes', value: routes })
  function addVdev(kind: VirtualDevice['kind']) {
    if (!vReady) { hintState.text = t('Turn on virtual routing in Settings first.'); return }
    const id = Math.max(0, ...vdevs.map(d => d.id)) + 1
    // Numbered per kind ("Virtual recording 1" even after "Virtual playback 1"); the id stays unique.
    const used = new Set(vdevs.filter(d => d.kind === kind).map(d => d.name))
    let n = 1
    while (used.has(t(kind === 'playback' ? 'Virtual playback {n}' : 'Virtual recording {n}', { n }))) n++
    const name = t(kind === 'playback' ? 'Virtual playback {n}' : 'Virtual recording {n}', { n })
    // A new playback device plays into Playback 1/2 until rewired, so it is never silent.
    putV([...vdevs, { id, name, kind }], kind === 'playback' ? [...vroutes, { from: `v:${id}`, to: 'hwout:0' }] : vroutes)
  }
  const removeVdev = (d: VirtualDevice) =>
    putV(vdevs.filter(x => x.id !== d.id), vroutes.filter(r => r.from !== `v:${d.id}` && r.to !== `v:${d.id}`))
  const renameVdev = (d: VirtualDevice, name: string) => putV(vdevs.map(x => (x.id === d.id ? { ...x, name } : x)))
  const routed = (from: string, to: string) => vroutes.some(r => r.from === from && r.to === to)
  const toggleRoute = (from: string, to: string) =>
    putV(vdevs, routed(from, to) ? vroutes.filter(r => !(r.from === from && r.to === to)) : [...vroutes, { from, to }])
  // Plugin endpoints <-> graph nodes. hwin: Analog, Mobile IN, Loopback 1/2, 3/4, 5/6 (driver input pairs);
  // hwout: Playback 1/2..7/8; apprec: the same five recording devices as seen by Windows apps.
  const LB = ['loopback12', 'loopback34', 'loopback56']
  /** Graph node <-> plugin endpoint, as [node, as source, as target]. */
  const vEnds = $derived([
    ...pairs.map(p => [pairNode(p.k), p.k < 2 ? `hwin:${p.k}` : `appin:${p.k - 2}`, p.k < 2 ? `apprec:${p.k}` : `hwout:${p.k - 2}`]),
    ...LB.map((l, i) => [`out:${l}`, `hwin:${i + 2}`, `apprec:${i + 2}`]),
    ...vplays.map(d => [`vplay:${d.id}`, `v:${d.id}`, undefined]),
    ...vrecs.map(d => [`vrec:${d.id}`, undefined, `v:${d.id}`]),
  ] as [string, string | undefined, string | undefined][])
  const vNode = (end: string, from: boolean) => vEnds.find(e => e[from ? 1 : 2] === end)?.[0]
  const vTarget = (node: string) => vEnds.find(e => e[0] === node)?.[2]
  // A split pair drags from its row dots (src:<row>), which belong to the pair's node.
  const vSource = (node: string) => vEnds.find(e => e[0] === node || pairs.some(p => pairNode(p.k) === e[0] && p.rows.some(r => `src:${r.key}` === node)))?.[1]
  // A refused restart: the changes are saved but wait for a re-plug — say so where they were made.
  let vrNoteHidden = $state<unknown>(null)
  const vrNote = $derived(
    !app.result || app.result.ok || vrNoteHidden === app.result ? null
    : app.result.op === 'virtualRouting' ? vrMessage(app.result.message)
    : app.result.op === 'renameEndpoint' ? t('Renaming the Windows device failed: {message}', { message: app.result.message ?? '' })
    : null)
  async function applyV() {
    if (!(await ask(t('Apply the virtual devices? The E2x2 restarts — sound stops for a moment, and Windows may reset default devices.'), t('Apply')))) return
    virtualRouting('apply')
  }
  /** Back to what was last applied (the host keeps it as JSON with PascalCase names). */
  function revertV() {
    try {
      const a = JSON.parse(app.profile!.virtualApplied ?? '{"devices":[],"routes":[]}')
      putV((a.devices ?? []).map((d: any) => ({ id: d.Id, name: d.Name, kind: d.Kind })),
        (a.routes ?? []).map((r: any) => ({ from: r.From, to: r.To })))
    } catch { /* nothing applied yet */ }
  }

  // ---- activity (0..1) -------------------------------------------------------------------
  function rowActivity(r: SourceRow): number {
    if (r.meter) return Math.max(...r.meter.map(k => meterPos(app.meters[k])))
    if (r.playback !== undefined) {
      // Driver meter covers ASIO too; the Windows endpoint meter is the fallback.
      const ep = byRole(`pb${r.playback}`)
      return Math.max(peakPos(app.winPeaks[`drv:pb${r.playback}`]), ep ? peakPos(app.winPeaks[ep.id]) : 0)
    }
    return 0
  }
  // Driver recording meters: Analog, Mobile IN, Loopback 1/2, 3/4, 5/6.
  const recLevel = (role: string) => peakPos(app.winPeaks[`drv:rec${LB.indexOf(role) + 2}`])
  /** Level a send carries into its mix (0..1 meter scale): source level + send level + pan law.
   *  The device does not meter sends; this is the same arithmetic its mixer applies. */
  function sendLevel(r: SourceRow, ch: { levelDb: number | null; pan: number }) {
    const pos = rowActivity(r)
    if (pos <= 0 || ch.levelDb === null) return 0
    const pan = Math.max(ch.pan, 100 - ch.pan) / 100 // linear pan: the louder side
    return dbPos(pos * 60 - 60 + ch.levelDb + 20 * Math.log10(Math.max(pan, 1e-4)))
  }
  const pairActivity = (p: PairView) => Math.max(...p.rows.map(rowActivity))
  const mixActivity = (m: number) =>
    Math.max(...mixMeters(m).map(meterPos))

  // ---- edges -----------------------------------------------------------------------------
  type Edge = {
    id: string; from: string; to: string; color: string; width: number
    activity: () => number; silent?: boolean; dashed?: boolean
    send?: { row: SourceRow; mix: number }
    output?: OutputKey
    vroute?: VirtualRoute
    /** A specific in-port inside the target node (a mix's send row); falls back to the node. */
    toPort?: string
  }

  // ---- sends: each MIX node lists the sources connected to it, with their level ------------
  const sendOn = (m: number, r: SourceRow) => { const ch = mixer.mixes[m].channel[r.channels[0]]; return !ch.mute && ch.levelDb !== null }
  function sendsOf(m: number) {
    const order = settings?.mixOrder?.[m] ?? []
    const rank = (r: SourceRow) => { const i = order.indexOf(r.key); return i < 0 ? 1000 + rows.indexOf(r) : i }
    return rows.filter(r => sendOn(m, r)).sort((a, b) => rank(a) - rank(b))
  }
  /** Drag a send row by its name to reorder it within its mix (display order only). */
  let reorder = $state<{ m: number; key: string; over: string | null } | null>(null)
  function reorderStart(e: PointerEvent, m: number, key: string) {
    if (e.button !== 0) return
    e.preventDefault()
    e.stopPropagation()
    reorder = { m, key, over: null }
    const move = (ev: PointerEvent) => {
      const el = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('[data-send]') as HTMLElement | null
      if (reorder) reorder = { ...reorder, over: el?.dataset.send?.startsWith(`${m}:`) ? el.dataset.send.slice(2) : null }
    }
    const up = () => {
      if (reorder?.over && reorder.over !== key) {
        const list = sendsOf(m).map(r => r.key).filter(k => k !== key)
        list.splice(list.indexOf(reorder.over), 0, key)
        set({ path: 'settings.mixOrder', value: { ...($state.snapshot(settings.mixOrder) ?? {}), [m]: list } })
      }
      reorder = null
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  // ---- peak warning: a mix that hits 0 dBFS lights up for a moment -------------------------
  let clipUntil = $state<Record<number, number>>({})
  let clock = $state(0)
  $effect(() => {
    const now = Date.now()
    for (let m = 0; m < MIXES.length; m++) {
      const peak = Math.max(...mixMeters(m).map(v => v ?? -999))
      if (peak >= -5) clipUntil[m] = now + 2000 // tenths of dB (sent in 0.5 dB steps): -0.5 dBFS
    }
  })
  $effect(() => { const id = setInterval(() => (clock = Date.now()), 500); return () => clearInterval(id) })
  const clipping = (m: number) => (clipUntil[m] ?? 0) > clock

  // ---- headphones: click = listen to this mix; hold = listen only while held -----------------
  let held: { prev: Source; prevMute: boolean; at: number } | null = null
  function listenDown(m: number) {
    const o = mixer.out12
    held = { prev: o.source, prevMute: o.muteL, at: Date.now() }
    connect(`mix:${m}`, 'out:out12')
  }
  function listenUp() {
    if (held && Date.now() - held.at > 450)
      set({ path: 'mixer.out12.source', value: held.prev }, { path: 'mixer.out12.muteL', value: held.prevMute }, { path: 'mixer.out12.muteR', value: held.prevMute })
    held = null
  }
  let collapsed = $state<Record<number, boolean>>({})
  const pairNode = (k: number) => `src:${pairs[k].rows[0].key}`

  /** Direct (non-mix) output sources -> the source rows they come from. */
  function rowsFor(src: Source): SourceRow[] {
    const pair = { In12: 0, In1: 0, In2: 0, MobileIn: 1, Playback12: 2, Playback34: 3, Playback56: 4, Playback78: 5 }[src as string]
    if (pair === undefined) return []
    const rs = rows.filter(r => r.pair === pair)
    if (src === 'In1') return rs.filter(r => r.channels.includes(0))
    if (src === 'In2') return rs.filter(r => r.channels.includes(1))
    return rs
  }
  // Hardware outputs first, then the loopbacks that feed Windows recording.
  const OUTPUT_ORDER = [...OUTPUTS.filter(o => !o.key.startsWith('loopback')), ...OUTPUTS.filter(o => o.key.startsWith('loopback'))]

  const edges = $derived.by(() => {
    const out: Edge[] = []
    // App wires: one per device the app plays into now; a silent app's dashed wire to where it sits.
    const endpointNode = (id: string) => {
      const p = pairs.find(x => x.k >= 2 && x.ep?.id === id)
      if (p) return pairNode(p.k)
      if (others.some(o => o.ep.id === id)) return `dev:${id}`
      const d = vplays.find(x => vEndpoint(x)?.id === id)
      return d ? `vplay:${d.id}` : undefined
    }
    for (const v of appViews) {
      for (const ep of v.active ? v.playingOn : [v.endpoint]) {
        const to = endpointNode(ep)
        const on = v.members.filter(m => m.endpoint === ep)
        if (to) out.push({ id: `a:${v.key}:${ep}`, from: `app:${v.key}`, to, color: 'var(--text-3)', width: 1.25,
          activity: () => Math.max(0, ...on.map(m => peakPos(app.winPeaks[m.key]))), dashed: !v.active })
      }
    }
    for (const p of pairs) {
      for (const h of p.asio)
        out.push({ id: `asio:${h.pid}`, from: `asio:${h.pid}`, to: pairNode(p.k), color: 'var(--text-3)', width: 1.25,
          activity: () => peakPos(app.winPeaks[`drv:pb${p.k - 2}`]) })
    }
    for (const row of rows) {
      for (let m = 0; m < MIXES.length; m++) {
        const ch = mixer.mixes[m].channel[row.channels[0]]
        if (ch.mute || ch.levelDb === null) continue
        const solo = mixer.mixes[m].channel.some(c => c.solo)
        const gain = toPos(ch.levelDb, 12)
        out.push({
          id: `s:${row.key}:${m}`, from: `src:${row.key}`, to: `mix:${m}`, toPort: `send:${m}:${row.key}`, color: mixColor(m),
          width: 1 + 2.5 * gain, activity: () => sendLevel(row, ch),
          silent: solo && !ch.solo, send: { row, mix: m },
        })
      }
    }
    for (const o of OUTPUTS) {
      const out_ = mixer[o.key]
      // A muted output counts as disconnected: no wire.
      if (outOff(o.key)) continue
      if (out_.source.startsWith('Mix')) {
        const m = 'ABCD'.indexOf(out_.source.slice(3))
        out.push({ id: `o:${o.key}`, from: `mix:${m}`, to: `out:${o.key}`, color: mixColor(m), width: 2, activity: () => mixActivity(m), output: o.key })
      } else {
        for (const r of rowsFor(out_.source))
          out.push({ id: `o:${o.key}:${r.key}`, from: `src:${r.key}`, to: `out:${o.key}`, color: 'var(--direct)', width: 1.5, activity: () => rowActivity(r), dashed: true, output: o.key })
      }
    }
    // Virtual device routes (plugin), dashed until applied.
    const pendingV = !!app.status.virtualPending
    for (const r of vroutes) {
      const from = vNode(r.from, true), to = vNode(r.to, false)
      if (from && to) out.push({ id: `v:${r.from}>${r.to}`, from, to, color: 'var(--virt)', width: 1.5, dashed: pendingV, vroute: r,
        activity: () => r.from.startsWith('v:') ? peakPos(app.winPeaks[vEndpoint(vdevs.find(d => `v:${d.id}` === r.from)!)?.id ?? '']) : 0 })
    }
    for (const d of vrecs)
      for (const s of recorders(`v:${d.id}`))
        out.push({ id: `r:${s.key}`, from: `vrec:${d.id}`, to: `rec:${s.key}`, color: 'var(--text-3)', width: 1.25,
          activity: () => peakPos(app.winPeaks[s.key]), dashed: !s.active })
    for (const o of OUTPUTS) {
      if (!o.key.startsWith('loopback')) continue
      for (const s of recorders(o.key))
        out.push({ id: `r:${s.key}`, from: `out:${o.key}`, to: `rec:${s.key}`, color: 'var(--text-3)', width: 1.25,
          activity: () => s.active ? Math.max(peakPos(app.winPeaks[s.key]), recLevel(o.key)) : 0, dashed: !s.active })
    }
    return out
  })

  // ---- geometry --------------------------------------------------------------------------
  let canvas: HTMLDivElement
  let grid: HTMLDivElement
  const nodes = new Map<string, HTMLElement>()
  const ports = new Map<string, HTMLElement>()
  let paths = $state<Record<string, string>>({})

  // Any node changing size can move others (column widths, wrapping text), so watch them all.
  let pending = 0
  const relayout = () => { clearTimeout(pending); pending = setTimeout(layout, 0) }
  const watcher = new ResizeObserver(relayout)

  function node(el: HTMLElement, id: string) {
    nodes.set(id, el)
    watcher.observe(el)
    return { destroy: () => { nodes.delete(id); watcher.unobserve(el) } }
  }
  /** A visible port (dot / ring) whose centre is where wires attach: "out:<id>" or "in:<id>". */
  function port(el: HTMLElement, id: string) {
    ports.set(id, el)
    return { update: (n: string) => { ports.delete(id); id = n; ports.set(id, el) }, destroy: () => ports.delete(id) }
  }

  // Positions are in the grid's own (unzoomed) units, since the wire layer is zoomed with it.
  const scale = () => grid.getBoundingClientRect().width / grid.offsetWidth || 1
  type Pt = { x: number; y: number }
  function box(el: HTMLElement) {
    const c = grid.getBoundingClientRect()
    const r = el.getBoundingClientRect()
    const s = scale()
    return { left: (r.left - c.left) / s, right: (r.right - c.left) / s, top: (r.top - c.top) / s, h: r.height / s, w: r.width / s }
  }
  function point(id: string, side: 'in' | 'out'): Pt | null {
    const p = ports.get(`${side}:${id}`)
    if (p) { const b = box(p); return { x: b.left + b.w / 2, y: b.top + b.h / 2 } }
    const el = nodes.get(id)
    if (!el) return null
    const b = box(el)
    return { x: side === 'out' ? b.right : b.left, y: b.top + b.h / 2 }
  }
  function spreadHeight(id: string, side: 'in' | 'out') {
    if (ports.get(`${side}:${id}`)) return 10
    const el = nodes.get(id)
    return el ? box(el).h * 0.6 : 0
  }

  function curve(a: Pt, b: Pt) {
    const dx = Math.max(40, (b.x - a.x) * 0.5)
    return `M${a.x},${a.y} C${a.x + dx},${a.y} ${b.x - dx},${b.y} ${b.x},${b.y}`
  }

  function layout() {
    if (!canvas || !grid) return
    // Fan wires out around each port, ordered by where the other end sits, so they don't cross.
    const ends = edges.map(e => {
      const own = e.toPort && ports.get(`in:${e.toPort}`) ? point(e.toPort, 'in') : null
      return { e, a: point(e.from, 'out'), b: own ?? point(e.to, 'in'), own: !!own }
    })
    const spread = (key: 'from' | 'to', side: 'a' | 'b', other: 'a' | 'b', portSide: 'in' | 'out') => {
      const groups = new Map<string, typeof ends>()
      // Wires that land on their own send row are already apart.
      for (const x of ends) if (x.a && x.b && !(side === 'b' && x.own)) groups.set(x.e[key], [...(groups.get(x.e[key]) ?? []), x])
      for (const [id, list] of groups) {
        if (list.length < 2) continue
        list.sort((p, q) => p[other]!.y - q[other]!.y)
        const span = Math.min(spreadHeight(id, portSide), (list.length - 1) * 6)
        list.forEach((x, i) => { x[side] = { ...x[side]!, y: x[side]!.y - span / 2 + (span * i) / (list.length - 1) } })
      }
    }
    spread('to', 'b', 'a', 'in')
    spread('from', 'a', 'b', 'out')
    const next: Record<string, string> = {}
    for (const { e, a, b } of ends) if (a && b) next[e.id] = curve(a, b)
    paths = next
  }

  $effect(() => {
    void edges, pairs, others, asio, zoom, vdevs, collapsed[0], collapsed[1], collapsed[2], collapsed[3]
    tick().then(layout)
  })
  $effect(() => {
    watcher.observe(canvas)
    watcher.observe(grid)
    document.fonts.ready.then(relayout)
    return () => watcher.disconnect()
  })

  // ---- zoom ------------------------------------------------------------------------------
  const zoom = $derived(settings?.routingZoom ?? 1)
  const clampZoom = (z: number) => Math.round(Math.min(2, Math.max(0.5, z)) * 100) / 100
  const setZoom = (z: number) => set({ path: 'settings.routingZoom', value: clampZoom(z) })
  function fit() {
    const natural = grid.scrollWidth / zoom
    setZoom(Math.min(1.5, (canvas.clientWidth - 8) / natural))
  }
  // Wheel over the background zooms around the cursor (faders handle their own wheel).
  function wheelZoom(e: WheelEvent) {
    if ((e.target as Element).closest('.menu, .pop, select')) return
    e.preventDefault()
    const r = canvas.getBoundingClientRect()
    const mx = e.clientX - r.left, my = e.clientY - r.top
    const z0 = zoom
    const z1 = clampZoom(z0 * (e.deltaY < 0 ? 1.1 : 1 / 1.1))
    if (z1 === z0) return
    const px = (canvas.scrollLeft + mx) / z0, py = (canvas.scrollTop + my) / z0
    setZoom(z1)
    tick().then(() => { canvas.scrollLeft = px * z1 - mx; canvas.scrollTop = py * z1 - my })
  }

  // Dragging the empty background moves the view.
  let panning = $state(false)
  function panStart(e: PointerEvent) {
    if (e.button !== 0 && e.button !== 1) return
    if ((e.target as Element).closest('.node, .app, button, input, select, .hit, .zoom')) return
    // preventDefault below keeps focus where it was; finish any edit (name, typed value) first.
    if (document.activeElement instanceof HTMLElement && document.activeElement.matches('input, textarea')) document.activeElement.blur()
    e.preventDefault()
    panning = true
    const sx = e.clientX, sy = e.clientY, l = canvas.scrollLeft, tp = canvas.scrollTop
    const move = (ev: PointerEvent) => { canvas.scrollLeft = l - (ev.clientX - sx); canvas.scrollTop = tp - (ev.clientY - sy) }
    const up = () => { panning = false; window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  // ---- dragging wires ----------------------------------------------------------------------
  let popover = $state<{ x: number; y: number; row: SourceRow; mix: number } | null>(null)
  let outPopover = $state<{ x: number; y: number; output: OutputKey; color: string } | null>(null)
  type DragKind = 'src' | 'mix' | 'app' | 'vplay'
  let drag = $state<{ from: string; kind: DragKind; x: number; y: number } | null>(null)
  let hoverTarget = $state<string | null>(null)

  function startDrag(e: PointerEvent, from: string, kind: DragKind) {
    e.preventDefault()
    e.stopPropagation()
    const pos = (ev: PointerEvent) => {
      const c = grid.getBoundingClientRect()
      const s = scale()
      return { x: (ev.clientX - c.left) / s, y: (ev.clientY - c.top) / s }
    }
    drag = { from, kind, ...pos(e) }
    const move = (ev: PointerEvent) => {
      if (!drag) return
      drag = { ...drag, ...pos(ev) }
      hoverTarget = dropTarget(ev)
    }
    const up = (ev: PointerEvent) => {
      const target = dropTarget(ev)
      if (drag && target) connect(drag.from, target)
      drag = null
      hoverTarget = null
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  /** Whether a node accepts the wire being dragged (also decides which hollow rings exist). */
  function accepts(id: string, kind = drag?.kind): boolean {
    if (!kind || id === drag?.from) return false
    switch (kind) {
      // Analog / Mobile IN can also feed a virtual recording device.
      case 'src': return id.startsWith('mix:') || id.startsWith('out:') || (id.startsWith('vrec:') && !!vSource(drag?.from ?? ''))
      case 'mix': return id.startsWith('out:')
      case 'app': return playsTo(id)
      case 'vplay': return !!vTarget(id)
    }
  }
  /** A node that is a Windows output: other devices and the E2x2 playback pairs. */
  const playsTo = (id: string) => id.startsWith('dev:') || (id.startsWith('src:') && rows.find(r => `src:${r.key}` === id)?.playback !== undefined)
    || (id.startsWith('vplay:') && !!vEndpoint(vdevs.find(d => `vplay:${d.id}` === id)!))

  function dropTarget(e: PointerEvent): string | null {
    const id = (document.elementFromPoint(e.clientX, e.clientY)?.closest('[data-node]') as HTMLElement | null)?.dataset.node
    return id && accepts(id) ? id : null
  }

  const SOURCE_OF_ROW: Record<number, Source> = { 0: 'In12', 1: 'MobileIn', 2: 'Playback12', 3: 'Playback34', 4: 'Playback56', 5: 'Playback78' }

  function connect(from: string, to: string) {
    if (from.startsWith('vplay:') || to.startsWith('vrec:')) {
      const a = vSource(from), b = vTarget(to)
      if (a && b && !routed(a, b)) toggleRoute(a, b)
      return
    }
    if (from.startsWith('app:')) {
      const s = viewOf(from)
      const ep = to.startsWith('dev:')
        ? endpoints.find(e => `dev:${e.id}` === to)
        : to.startsWith('vplay:') ? vEndpoint(vdevs.find(d => `vplay:${d.id}` === to)!)
        : byRole(`pb${rows.find(r => `src:${r.key}` === to)?.playback}`)
      if (s && ep) moveApp(s, ep.id)
    } else if (from.startsWith('src:') && to.startsWith('mix:')) {
      const row = rows.find(r => `src:${r.key}` === from)!
      const m = Number(to.slice(4))
      set(...row.channels.flatMap(c => [
        { path: `mixer.mixes.${m}.channel.${c}.mute`, value: false },
        ...(mixer.mixes[m].channel[c].levelDb === null ? [{ path: `mixer.mixes.${m}.channel.${c}.levelDb`, value: 0 }] : []),
      ]))
    } else if (to.startsWith('out:')) {
      const key = to.slice(4) as OutputKey
      let value: Source
      if (from.startsWith('mix:')) value = `Mix${MIXES[Number(from.slice(4))]}` as Source
      else {
        const row = rows.find(r => `src:${r.key}` === from)!
        value = row.pair === 0 && row.channels.length === 1 ? (row.channels[0] === 0 ? 'In1' : 'In2') : SOURCE_OF_ROW[row.pair]
      }
      setOutSource(key, value)
    }
  }

  /** Right-click on a wire removes that connection. */
  function disconnect(e: MouseEvent, edge: Edge) {
    e.preventDefault()
    closeAll()
    if (edge.vroute) {
      toggleRoute(edge.vroute.from, edge.vroute.to)
    } else if (edge.send) {
      setSend(edge.send.mix, edge.send.row.channels, 'mute', true)
    } else if (edge.output) {
      muteOut(edge.output, true)
    } else if (edge.from.startsWith('app:')) {
      // An app always plays somewhere; "disconnect" returns it to the Windows default.
      const s = viewOf(edge.from)
      if (s?.pinned) moveApp(s, null)
    } else if (edge.from.startsWith('asio:')) {
      const h = asio?.hosts.find(x => `asio:${x.pid}` === edge.from)
      // -1 = cleared by the user: not auto-detected again until assigned from the menu.
      if (h) set({ path: `asioRoutes.${h.name}`, value: -1 })
    }
  }

  function openEdge(e: MouseEvent, edge: Edge) {
    const at = { x: e.clientX, y: e.clientY }
    if (edge.send) popover = { ...at, ...edge.send }
    else if (edge.output) outPopover = { ...at, output: edge.output, color: edge.color }
  }

  /** User-facing name of a Windows output: the E2x2 source name, or the device name. */
  function endpointLabel(id: string) {
    const ep = endpoints.find(e => e.id === id)
    const pb = ep?.e2x2?.startsWith('pb') ? Number(ep.e2x2.slice(2)) : undefined
    return pb === undefined ? (ep?.name ?? t('Unknown')) : pairName(pairs[pb + 2])
  }

  // ---- right-click menus: every action a node has ----------------------------------------
  let ctx = $state<{ x: number; y: number; title: string; items: Item[] } | null>(null)
  const closeAll = () => { popover = outPopover = ctx = null }
  function openCtx(e: MouseEvent, title: string, items: Item[]) {
    e.preventDefault()
    e.stopPropagation()
    closeAll()
    ctx = { x: e.clientX, y: e.clientY, title, items }
  }

  /** Windows items for a node backed by a Windows device: default device choices. */
  function windowsItems(ep: WinEndpoint | undefined): Item[] {
    if (!ep) return []
    const kind = t(ep.flow === 'render' ? 'playback' : 'recording')
    return [{ separator: true }, { section: `Windows · ${ep.name}` },
      { label: t('Default {kind} device', { kind }), checked: ep.isDefault, action: () => setDefaultDevice(ep.id, false) },
      { label: t('Default communications device'), checked: ep.isDefaultComm, action: () => setDefaultDevice(ep.id, true) }]
  }


  function pairMenu(e: MouseEvent, p: PairView) {
    const items: Item[] = []
    for (const row of p.rows) {
      const label = p.rows.length > 1 ? channelLabel(row) : ''
      items.push({ section: label ? `${label} → ${t('Connect to')}` : t('Connect to') })
      MIXES.forEach((_, m) => items.push({
        label: mixName(m), checked: sendOn(m, row),
        action: () => sendOn(m, row)
          ? setSend(m, row.channels, 'mute', true)
          : connect(`src:${row.key}`, `mix:${m}`),
      }))
    }
    items.push({ section: t('Send directly to') })
    for (const o of OUTPUTS) {
      const direct = !mixer[o.key].source.startsWith('Mix') && rowsFor(mixer[o.key].source).some(r => r.pair === p.k)
      items.push({ label: outName(o.key, o.name), checked: direct && !outOff(o.key), action: () => connect(`src:${p.rows[0].key}`, `out:${o.key}`) })
    }
    items.push({ separator: true }, {
      label: t(mixer.mixes[0].link[p.k] ? 'Split into two mono channels' : 'Link as stereo'),
      action: () => toggleLink(p.k),
    })
    openCtx(e, pairName(p), [...items, ...windowsItems(p.ep)])
  }

  function mixMenu(e: MouseEvent, m: number) {
    const src = `Mix${MIXES[m]}`
    const items: Item[] = [{ section: t('Route to') }]
    for (const o of OUTPUTS)
      items.push({ label: outName(o.key, o.name), checked: mixer[o.key].source === src && !outOff(o.key), action: () => connect(`mix:${m}`, `out:${o.key}`) })
    items.push({ separator: true }, { label: t('Listen on OUT 1+2'), checked: mixer.out12.source === src, action: () => connect(`mix:${m}`, 'out:out12') })
    openCtx(e, mixName(m), items)
  }

  function outMenu(e: MouseEvent, key: OutputKey, fallback: string) {
    const o = mixer[key]
    const off = outOff(key)
    const pick = (value: Source) => setOutSource(key, value)
    const items: Item[] = [{ section: t('Source') }]
    MIXES.forEach((mx, m) => items.push({ label: mixName(m), checked: !off && o.source === `Mix${mx}`, action: () => pick(`Mix${mx}` as Source) }))
    for (const s of DIRECT_SOURCES) items.push({ label: SOURCE_LABEL[s], checked: !off && o.source === s, action: () => pick(s) })
    items.push({ separator: true }, {
      label: t(off ? 'Reconnect' : 'Disconnect'),
      action: () => muteOut(key, !off),
    })
    const extra = key.startsWith('loopback') ? windowsItems(byRole(key)) : []
    openCtx(e, outName(key, fallback), [...items, ...extra])
  }

  function appMenu(e: MouseEvent, s: WinSession) {
    const items: Item[] = []
    const kind = s.flow === 'capture' || s.system ? undefined : appReason(s).kind
    if (kind === 'app') {
      items.push({ section: t('Output device') },
        { label: t('Chosen inside {name}: change it in its own audio settings', { name: s.name }), disabled: true, action: () => {} },
        { separator: true })
    } else if (kind === 'comm') {
      // The communications device is set per device (Playback node menu), not per app.
      items.push({ section: t('Output device') },
        { label: t('Follows the Windows communications device: change it on a Playback node (right-click)'), disabled: true, action: () => {} },
        { separator: true })
    } else if (kind) {
      items.push({ section: t('Output device · whole app') }, { label: t('Windows default'), checked: !s.pinned, action: () => moveApp(s, null) })
      for (const ep of endpoints.filter(x => x.flow === 'render'))
        items.push({ label: endpointLabel(ep.id), sub: ep.e2x2 ? ep.name : ep.device, checked: s.pinned === ep.id, action: () => moveApp(s, ep.id) })
      items.push({ separator: true })
    }
    items.push({ label: t(s.muted ? 'Unmute' : 'Mute'), action: () => setAppLevel(s, undefined, !s.muted) })
    openCtx(e, s.name, items)
  }

  function asioMenu(e: MouseEvent, h: AsioHost) {
    const items: Item[] = [{ section: t('Assign to') }]
    for (const p of pairs.filter(p => p.k >= 2))
      items.push({ label: pairName(p), sub: p.ep?.name, checked: asioPair(h.name) === p.k - 2, action: () => set({ path: `asioRoutes.${h.name}`, value: p.k - 2 }) })
    items.push({ separator: true }, { label: t('Forget'), action: () => set({ path: `asioRoutes.${h.name}`, value: -1 }) })
    openCtx(e, `${h.name} · ASIO`, items)
  }

  function vMenu(e: MouseEvent, d: VirtualDevice) {
    const self = `v:${d.id}`
    const items: Item[] = []
    // Analog 1/2, Mobile IN, Loopback 1/2..5/6 (as named in the graph), and the four playback pairs.
    const recs = [pairName(pairs[0]), pairName(pairs[1]), ...LB.map(l => outName(l as OutputKey, l))]
    const pbs = pairs.filter(p => p.k >= 2)
    if (d.kind === 'playback') {
      items.push({ section: t('Into the E2x2 mixer') })
      pbs.forEach(p => items.push({ label: pairName(p), checked: routed(self, `hwout:${p.k - 2}`), action: () => toggleRoute(self, `hwout:${p.k - 2}`) }))
      items.push({ section: t('Into a recording device') })
      recs.forEach((n, i) => items.push({ label: n, checked: routed(self, `apprec:${i}`), action: () => toggleRoute(self, `apprec:${i}`) }))
      if (vrecs.length) {
        items.push({ section: t('Into a virtual recording device') })
        vrecs.forEach(r => items.push({ label: vName(r), checked: routed(self, `v:${r.id}`), action: () => toggleRoute(self, `v:${r.id}`) }))
      }
    } else {
      items.push({ section: t('From Windows playback') })
      pbs.forEach(p => items.push({ label: pairName(p), checked: routed(`appin:${p.k - 2}`, self), action: () => toggleRoute(`appin:${p.k - 2}`, self) }))
      items.push({ section: t('From the E2x2') })
      recs.forEach((n, i) => items.push({ label: n, checked: routed(`hwin:${i}`, self), action: () => toggleRoute(`hwin:${i}`, self) }))
      if (vplays.length) {
        items.push({ section: t('From a virtual playback device') })
        vplays.forEach(p => items.push({ label: vName(p), checked: routed(`v:${p.id}`, self), action: () => toggleRoute(`v:${p.id}`, self) }))
      }
    }
    items.push({ separator: true }, { label: t('Remove device'), action: () => removeVdev(d) })
    openCtx(e, vName(d), [...items, ...windowsItems(vEndpoint(d))])
  }

  function deviceMenu(e: MouseEvent, ep: WinEndpoint) {
    openCtx(e, ep.name, windowsItems(ep).slice(1))
  }

  // One link switch per source, applied to all four mixes.
  function toggleLink(pair: number) {
    const linked = !mixer.mixes[0].link[pair]
    const changes = []
    for (let m = 0; m < 4; m++) {
      changes.push({ path: `mixer.mixes.${m}.link.${pair}`, value: linked })
      if (linked) {
        const l = mixer.mixes[m].channel[2 * pair]
        for (const f of ['levelDb', 'mute', 'solo', 'invert'] as const)
          changes.push({ path: `mixer.mixes.${m}.channel.${2 * pair + 1}.${f}`, value: l[f] })
        changes.push({ path: `mixer.mixes.${m}.channel.${2 * pair}.pan`, value: 0 })
        changes.push({ path: `mixer.mixes.${m}.channel.${2 * pair + 1}.pan`, value: 100 })
      }
    }
    set(...changes)
  }

  // ---- node controls -----------------------------------------------------------------------
  // Inputs on the device: channel 0 = IN 1 (inputs[0]), 1 = IN 2 (inputs[2]), 2/3 = Mobile IN L/R (inputs[1]/[3]).
  const INPUT_OF_CHANNEL = [0, 2, 1, 3]
  const inputsOf = (r: SourceRow) => r.channels.map(c => INPUT_OF_CHANNEL[c])
  const inp = (i: number, f: string, value: unknown) => ({ path: `mixer.inputs.${i}.${f}`, value })
  const setInputs = (r: SourceRow, f: string, value: unknown) => set(...inputsOf(r).map(i => inp(i, f, value)))
  const JACK = { headphone: 'Phones', line: 'Line', aux: 'Aux' } as const

  const initials = (n: string) => n.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase()

  /** Hover text: what a node does, then the pointer to its right-click menu. */
  const more = (what: string) => () => `${t(what)} · ${t('Right-click for more')}`
</script>

<div class="wrap">
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div class="routing" class:dragging={!!drag} class:panning bind:this={canvas} onwheel={wheelZoom} onscroll={closeAll}
  onpointerdown={panStart} use:hint={() => t('Wheel: zoom · drag the background: move')}>
  <div class="grid" bind:this={grid} style:zoom={zoom}>
  <!-- Wire layer sized by CSS to the content, so it never holds the scroll area open. -->
  <svg class="wires" aria-hidden="true">
    {#each edges as e (e.id)}
      {#if paths[e.id]}
        <!-- Quantised so a level that barely moves does not repaint the wire. -->
        {@const a = e.silent ? 0 : Math.round(e.activity() * 20) / 20}
        <g class="wire" class:silent={e.silent} class:dashed={e.dashed} style:--c={e.color}>
          <path class="base" d={paths[e.id]} stroke-width={e.width} />
          <path class="glow" d={paths[e.id]} stroke-width={e.width} style:opacity={a} />
          <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
          <path class="hit" d={paths[e.id]} onclick={ev => openEdge(ev, e)} oncontextmenu={ev => disconnect(ev, e)} use:hint={() => t(e.send || e.output ? 'Click: adjust level · Right-click: disconnect' : 'Right-click: disconnect')} />
        </g>
      {/if}
    {/each}
    {#if drag}
      {@const a = point(drag.from, 'out')}
      {#if a}<path class="drag" d={curve(a, { x: drag.x, y: drag.y })} />{/if}
    {/if}
  </svg>

    <!-- Zones: Windows (apps that play) | the E2x2 itself | Windows (apps that record) -->
    <div class="band win-l"></div>
    <div class="band dev"></div>
    <div class="band win-r"></div>
    {#if others.length}<div class="band other"></div>{/if}
    <div class="headrow">
    <div class="colhead apps-h"><span class="zone">{t('Windows playback')}</span><span class="label">{t('Apps')}</span></div>
    <div class="colhead src-h">
      <span class="zone">E2x2 OTG</span>
      <span class="label">{t('Sources')}</span>
    </div>
    <div class="colhead mix-h"><span class="zone">&nbsp;</span><span class="label">{t('Mixes')}</span></div>
    <div class="colhead out-h"><span class="zone">&nbsp;</span><span class="label">{t('Outputs')}</span></div>
    <div class="colhead rec-h"><span class="zone">{t('Windows recording')}</span><span class="label">{t('Recorded by')}</span></div>
    </div>

    {#snippet outDot(id: string, kind: DragKind, label: string)}
      <button class="dot-out" use:port={`out:${id}`} aria-label={label} onpointerdown={e => startDrag(e, id, kind)}></button>
    {/snippet}
    {#snippet inRing(id: string)}
      <span class="ring-in" class:hot={hoverTarget === id} use:port={`in:${id}`}></span>
    {/snippet}

    {#snippet appNode(s: WinSession)}
      <div class="app" class:idle={!s.active} class:muted={s.muted} class:origin={drag?.from === `app:${s.key}`}
        use:node={`app:${s.key}`} use:hint={more('Chip: output device · Drag the dot onto a source or device to move the app')} oncontextmenu={e => appMenu(e, s)} role="group">
        <div class="app-top">
          {@render appTop(s.icon, s.name, procKind(s))}
          {#if s.system}
            <span class="chip fixed">{t('Default only')}</span>
          {:else}
            {@const why = appReason(s)}
            <button class="chip" class:pinned={!!s.pinned && s.pinned === s.endpoint} aria-label={t('Choose where {name} plays', { name: s.name })}
              use:hint={() => `${why.hint} · ${t('Click to choose where it plays')}`} onclick={e => appMenu(e, s)}>
              {why.label}<span class="caret">▾</span>
            </button>
          {/if}
        </div>
        <Fader compact volume value={s.volume}
          label={t('Volume')} dim={s.muted} onchange={v => setAppLevel(s, v ?? 0)} />
        <span class="app-level" style:--p={appPeak(s)}></span>
        {#if movable(s)}{@render outDot(`app:${s.key}`, 'app', `Move ${s.name}`)}{/if}
      </div>
    {/snippet}

    {#snippet appTop(icon: string | null, name: string, kind?: string)}
      {#if icon}<img src={icon} alt="" />{:else}<span class="ini">{initials(name)}</span>{/if}
      <span class="app-name">{name}</span>{#if kind}<span class="app-kind">{kind}</span>{/if}
    {/snippet}

    <!-- An app recording from a Windows recording device (loopback or virtual). -->
    {#snippet recNode(s: WinSession)}
      <div class="app rec" class:idle={!s.active} class:svc={s.service} class:muted={s.muted} use:node={`rec:${s.key}`}
        use:hint={() => t('This app records from this output through Windows')} oncontextmenu={e => appMenu(e, s)} role="group">
        <div class="app-top">{@render appTop(s.icon, s.name)}</div>
        <Fader compact volume value={s.volume} label={t('Volume')} dim={s.muted} onchange={v => setAppVolume(s.key, v ?? 0)} />
        <span class="app-level" style:--p={peakPos(app.winPeaks[s.key])}></span>
      </div>
    {/snippet}

    {#snippet asioNode(h: AsioHost)}
      <div class="app asio-app" use:node={`asio:${h.pid}`} use:hint={more('ASIO output is chosen inside the app; the graph detects which Playback it uses.')} oncontextmenu={e => asioMenu(e, h)} role="group">
        <div class="app-top">
          {@render appTop(h.icon, h.name)}
          <span class="chip fixed">{t('ASIO · set in app')}</span>
        </div>
        <span class="app-level" style:--p={asioPair(h.name) === undefined ? 0 : peakPos(app.winPeaks[`drv:pb${asioPair(h.name)}`])}></span>
      </div>
    {/snippet}


    {#snippet inputControls(r: SourceRow)}
      {@const i = inputsOf(r)[0]}
      {@const c = mixer.inputs[i]}
      <div class="frow"><span class="flabel">{t('Gain')}</span>
        <Fader compact value={c.gainDb} min={0} max={20} linear color="var(--text)" label="{t('Gain')} {rowName(r)}"
          onchange={v => setInputs(r, 'gainDb', v ?? 0)} /></div>
      <div class="toggles">
        {#if r.pair === 0}
          <button class="tg warn" class:on={c.phantom48V} use:hint={() => t('Phantom power — pops when switched')} onclick={() => setInputs(r, 'phantom48V', !c.phantom48V)}>48V</button>
          <button class="tg" class:on={c.instrument} use:hint={() => t('Instrument (Hi-Z) input')} onclick={() => setInputs(r, 'instrument', !c.instrument)}>INST</button>
          <button class="tg" class:on={c.monitor} use:hint={() => t('Direct monitoring')} onclick={() => setInputs(r, 'monitor', !c.monitor)}>MON</button>
        {/if}
        <button class="tg mute" class:on={c.mute} use:hint={() => t('Mute input')} onclick={() => setInputs(r, 'mute', !c.mute)}>M</button>
      </div>
    {/snippet}

    <div class="lanes">
      {#if asioUnassigned.length}
        <div class="lane">
          <div class="apps">{#each asioUnassigned as h (h.pid)}{@render asioNode(h)}{/each}</div>
          <span class="hint">{t('Detecting which Playback this ASIO app uses… play some audio in it, or assign it from the right-click menu.')}</span>
        </div>
      {/if}

      {#each pairs as p (p.k)}
        {@const id = pairNode(p.k)}
        {#if p.k === 0 || p.k === 2}
          <div class="group-h"><span class="label">{t(p.k === 0 ? 'Inputs (hardware)' : 'Windows playback → E2x2')}</span></div>
        {/if}
        {@const recs = p.k < 2 ? recorders(p.role) : []}
        <div class="lane">
          <div class="apps">
            {#each p.asio as h (h.pid)}{@render asioNode(h)}{/each}
            {#each p.apps as s (s.key)}{@render appNode(s)}{/each}
          </div>
          <div class="node src" class:drop={accepts(id)} class:target={hoverTarget === id}
            class:origin={p.rows.some(r => drag?.from === `src:${r.key}`)} data-node={id} use:node={id}
            oncontextmenu={e => pairMenu(e, p)} use:hint={more('Drag a dot onto a mix or an output to connect')} role="group">
            {#if playsTo(id)}{@render inRing(id)}{/if}
            <div class="n-top">
              <span class="n-name"><Name key="pair{p.k}" fallback={PAIRS[p.k].name} display={pairName(p)}
                windows={sync && !!p.ep} onrename={v => renamePair(p, v)} /></span>
              {#if p.ep?.isDefault}<span class="badge">{t('Default')}</span>{/if}
              {#if p.ep?.isDefaultComm}<span class="badge">{t('Comm')}</span>{/if}
            </div>
            <span class="n-sub">
              {#if p.ep}{t(p.k < 2 ? 'Windows rec · {name}' : 'Windows · {name}', { name: p.ep.name })}{:else}{t('Hidden in Windows')}{/if}
            </span>
            {#if p.k >= 2 && p.ep}
              <!-- Playback pairs: the Windows device volume (what Windows' own slider sets). -->
              <div class="frow" use:hint={() => t('Windows volume')}><span class="flabel">Win</span>
                <Fader compact volume value={p.ep.volume} label={t('Windows volume')} dim={p.ep.muted}
                  onchange={v => setEndpointVolume(p.ep!.id, v ?? 0)} /></div>
            {/if}
            {#if p.rows.length === 1}
              {#if p.k < 2}{@render inputControls(p.rows[0])}{/if}
              <div class="act" style:--p={pairActivity(p)}></div>
              {@render outDot(`src:${p.rows[0].key}`, 'src', `Connect ${pairName(p)}`)}
            {:else}
              {#each p.rows as r (r.key)}
                <div class="chan">
                  <span class="chan-label">{channelLabel(r)}</span>
                  <div class="chan-body">
                    {#if p.k < 2}{@render inputControls(r)}{/if}
                    <div class="act" style:--p={rowActivity(r)}></div>
                  </div>
                  {@render outDot(`src:${r.key}`, 'src', `Connect ${rowName(r)}`)}
                </div>
              {/each}
            {/if}
            {#if recs.length}
              <div class="recby">
                {#each recs as s (s.key)}
                  <span class="recapp" class:idle={!s.active} class:svc={s.service}>{#if s.icon}<img src={s.icon} alt="" />{/if}{s.name}</span>
                {/each}
              </div>
            {/if}
          </div>
        </div>
      {/each}

    </div>

    <div class="others">
      {#if others.length}
        <div class="group-h other-h"><span class="zone">{t('Other Windows outputs')}</span></div>
        {#each others as o (o.ep.id)}
          {@const id = `dev:${o.ep.id}`}
          <div class="lane">
            <div class="apps">{#each o.apps as s (s.key)}{@render appNode(s)}{/each}</div>
            <div class="node dev" class:drop={accepts(id)} class:target={hoverTarget === id} data-node={id} use:node={id}
              oncontextmenu={e => deviceMenu(e, o.ep)} use:hint={more('Another Windows output. Drop an app here to move it.')} role="group">
              {@render inRing(id)}
              <div class="n-top">
                <span class="n-name"><span class="plain">{o.ep.name}</span></span>
                {#if o.ep.isDefault}<span class="badge">{t('Default')}</span>{/if}
                {#if o.ep.isDefaultComm}<span class="badge">{t('Comm')}</span>{/if}
              </div>
              <span class="n-sub">{o.ep.device}</span>
              <Fader compact volume value={o.ep.volume}
                label={t('Windows volume')} dim={o.ep.muted}
                onchange={v => setEndpointVolume(o.ep.id, v ?? 0)} />
              <div class="act" style:--p={peakPos(app.winPeaks[o.ep.id])}></div>
            </div>
          </div>
        {/each}
      {/if}
    </div>

    <div class="mixes">
      {#each MIXES as mx, m (mx)}
        {@const listening = mixer.out12.source === `Mix${mx}`}
        {@const sends = sendsOf(m)}
        {@const id = `mix:${m}`}
        <div class="node mix" class:clip={clipping(m)} class:drop={accepts(id)} class:origin={drag?.from === id} class:target={hoverTarget === id}
          data-node={id} use:node={id} style:--c={mixColor(m)} oncontextmenu={e => mixMenu(e, m)} use:hint={more('Drag the dot onto an output · Headphones icon: listen on OUT 1+2')} role="group">
          {@render inRing(id)}
          <div class="n-top">
            <span class="swatch"></span>
            <span class="n-name"><Name key="mix{m}" fallback="MIX {mx}" /></span>
            {#if clipping(m)}<span class="clipbadge" use:hint={() => t('This mix reached 0 dBFS — lower the loudest send')}>{t('Peak')}</span>{/if}
            <button class="listen" class:on={listening} aria-label={t(listening ? 'OUT 1+2 is playing this mix' : 'Listen on OUT 1+2')}
              use:hint={() => t('Click: listen on OUT 1+2 · Hold: listen only while held')}
              onpointerdown={() => listenDown(m)} onpointerup={listenUp} onpointerleave={listenUp}><Icon name="headphones" /></button>
          </div>
          <Meter values={mixMeters(m)} />
          {#if sends.length}
            <button class="fold-sends" onclick={() => (collapsed[m] = !(collapsed[m] ?? true))} aria-expanded={!(collapsed[m] ?? true)}
              use:hint={() => t('Sources connected to this mix — show / hide their levels')}>
              <span class="chev" class:open={!(collapsed[m] ?? true)}>›</span>{t('{n} sources', { n: sends.length })}
            </button>
            {#if !(collapsed[m] ?? true)}
              <div class="sends">
                {#each sends as r (r.key)}
                  {@const ch = mixer.mixes[m].channel[r.channels[0]]}
                  {@const silenced = mixer.mixes[m].channel.some(c => c.solo) && !ch.solo}
                  <div class="send" class:silenced class:over={reorder?.m === m && reorder.over === r.key} class:moving={reorder?.m === m && reorder.key === r.key}
                    data-send="{m}:{r.key}" use:hint={() => t('Level from {src} into this mix · S: solo · ×: disconnect · drag the name to reorder', { src: rowName(r) })}>
                    <span class="send-in" use:port={`in:send:${m}:${r.key}`}></span>
                    <!-- svelte-ignore a11y_no_static_element_interactions -->
                    <span class="send-name grab" onpointerdown={e => reorderStart(e, m, r.key)}>{sendName(r)}</span>
                    <Fader compact value={ch.levelDb} min={-89} max={12} color={mixColor(m)} dim={silenced}
                      label="{rowName(r)} → {mixName(m)}" onchange={v => setSend(m, r.channels, 'levelDb', v)} />
                    <button class="ms" class:solo={ch.solo} aria-label={t('Solo in this mix')} onclick={() => setSend(m, r.channels, 'solo', !ch.solo)}>S</button>
                    <button class="ms x" aria-label={t('Disconnect')} onclick={() => setSend(m, r.channels, 'mute', true)}>×</button>
                  </div>
                {/each}
              </div>
            {/if}
          {/if}
          {@render outDot(id, 'mix', `Route ${mixName(m)}`)}
        </div>
      {/each}

    </div>

    <div class="outputs">
      {#each OUTPUT_ORDER as o (o.key)}
        {#if o.key === 'out12' || o.key === 'loopback12'}
          <div class="group-h out-g"><span class="label">{t(o.key === 'out12' ? 'Hardware outputs' : 'Loopback → Windows recording')}</span></div>
        {/if}
        {@const out_ = mixer[o.key]}
        {@const rec = o.key.startsWith('loopback') ? byRole(o.key) : undefined}
        {@const id = `out:${o.key}`}
        {@const path = `mixer.${o.key}`}
        {@const off = outOff(o.key)}
        <div class="out-row">
          <div class="node out" class:drop={accepts(id)} class:target={hoverTarget === id} class:muted={off}
            data-node={id} use:node={id} role="group" oncontextmenu={e => outMenu(e, o.key, o.name)} use:hint={more('Drop a mix on the ring to choose what this output plays · M: disconnect')}>
            {@render inRing(id)}
            <div class="n-top">
              <span class="n-name"><Name key="out:{o.key}" fallback={o.name} display={outName(o.key, o.name)}
                windows={sync && !!rec} onrename={v => sync && rec && v !== rec.name && renameEndpoint(rec.id, v)} /></span>
              {#if rec?.isDefault}<span class="badge">{t('Default')}</span>{/if}
              <button class="m" class:on={off} aria-label={t(off ? 'Reconnect' : 'Disconnect')}
                onclick={() => muteOut(o.key, !off)}>M</button>
            </div>
            <span class="n-sub">
              {#if off}{t('Not connected')}{:else}{sourceName(out_.source)}{/if}{#if rec} · {t('Windows rec · {name}', { name: rec.name })}{/if}
            </span>
            {#if out_.link}
              <Fader compact value={out_.levelDbL} min={-89} max={0} label="{outName(o.key, o.name)} level" dim={off} color="var(--text-2)"
                onchange={v => setOutLevel(o.key, v)} />
            {:else}
              <div class="lr"><span>L</span><Fader compact value={out_.levelDbL} min={-89} max={0} label="{o.name} L" dim={off} color="var(--text-2)"
                onchange={v => set({ path: `${path}.levelDbL`, value: v })} /></div>
              <div class="lr"><span>R</span><Fader compact value={out_.levelDbR} min={-89} max={0} label="{o.name} R" dim={off} color="var(--text-2)"
                onchange={v => set({ path: `${path}.levelDbR`, value: v })} /></div>
            {/if}
            <div class="toggles">
              {#each o.jacks ?? [] as j (j)}
                <button class="tg" class:on={out_[j]} use:hint={() => t('{jack} output on/off', { jack: JACK[j] })}
                  onclick={() => set({ path: `${path}.${j}`, value: !out_[j] })}>{JACK[j]}</button>
              {/each}
              {#if o.phoneGain}
                <button class="tg" class:on={mixer[o.phoneGain]} use:hint={() => t('Headphone output level: +17 dBu (off = 0 dBu)')}
                  onclick={() => set({ path: `mixer.${o.phoneGain}`, value: !mixer[o.phoneGain!] })}>+17</button>
              {/if}
              <span class="spacer"></span>
              <button class="tg icon" class:on={out_.link} use:hint={() => t(out_.link ? 'Unlink L/R' : 'Link L/R')}
                onclick={() => set({ path: `${path}.link`, value: !out_.link }, { path: `${path}.levelDbR`, value: out_.levelDbL }, { path: `${path}.muteR`, value: out_.muteL })}>
                <Icon name={out_.link ? 'link' : 'unlink'} size={12} />
              </button>
            </div>
          </div>
          <div class="recs">
            {#each o.key.startsWith('loopback') ? recorders(o.key) : [] as s (s.key)}
              {@render recNode(s)}
            {/each}
          </div>
        </div>
      {/each}

    </div>

    {#if vOn}
    <!-- Virtual devices live in the driver's mixer, not in the E2x2: their own zone, under the hardware. -->
    <div class="band virt"></div>
    <div class="vzone-l">
      <div class="group-h"><span class="zone vz">{t('Virtual devices')}</span>
        <span class="label">{t('Mixed inside the driver — separate from the E2x2 mixes')}</span></div>
      {#each vplays as d (d.id)}
        {@const id = `vplay:${d.id}`}
        {@const ep = vEndpoint(d)}
        <div class="lane">
          <div class="apps">{#each renderSessions(ep) as s (s.key)}{@render appNode(s)}{/each}</div>
          <div class="node src virt" class:drop={accepts(id)} class:target={hoverTarget === id} class:origin={drag?.from === id}
            data-node={id} use:node={id} oncontextmenu={e => vMenu(e, d)} use:hint={more('Virtual playback device: apps can play to it; drag its dot to where its sound goes.')} role="group">
            {#if playsTo(id)}{@render inRing(id)}{/if}
            <div class="n-top">
              <span class="n-name"><Name key="vdev{d.id}" fallback={vName(d)} display={vName(d)} onrename={v => renameVdev(d, v)} /></span>
              <span class="badge vbadge">{t('Virtual')}</span>
            </div>
            <span class="n-sub">{ep ? t('Windows · {name}', { name: ep.name }) : t('Not applied yet')}</span>
            {#if ep}
              <Fader compact volume value={ep.volume}
                label={t('Windows volume')} dim={ep.muted} onchange={v => setEndpointVolume(ep.id, v ?? 0)} />
              <div class="act" style:--p={peakPos(app.winPeaks[ep.id])}></div>
            {/if}
            {@render outDot(id, 'vplay', `Route ${vName(d)}`)}
          </div>
        </div>
      {/each}
      <div class="lane">
        <span></span>
        <button class="add-v" class:off={!vReady} onclick={() => addVdev('playback')}
          use:hint={() => t(vReady ? 'Add a Windows playback device (virtual channel of the E2x2 driver)' : 'Turn on virtual routing in Settings first.')}>+ {t('Playback device')}</button>
      </div>

    </div>
    <div class="vzone-r">
      {#each vrecs as d (d.id)}
        {@const id = `vrec:${d.id}`}
        {@const ep = vEndpoint(d)}
        <div class="out-row">
          <div class="node out virt" class:drop={accepts(id)} class:target={hoverTarget === id}
            data-node={id} use:node={id} role="group" oncontextmenu={e => vMenu(e, d)} use:hint={more('Virtual recording device: drop a virtual playback device or Analog / Mobile IN on the ring.')}>
            {@render inRing(id)}
            <div class="n-top">
              <span class="n-name"><Name key="vdev{d.id}" fallback={vName(d)} display={vName(d)} onrename={v => renameVdev(d, v)} /></span>
              <span class="badge vbadge">{t('Virtual')}</span>
            </div>
            <span class="n-sub">{ep ? t('Windows rec · {name}', { name: ep.name }) : t('Not applied yet')}</span>
          </div>
          <div class="recs">
            {#each recorders(`v:${d.id}`) as s (s.key)}
              {@render recNode(s)}
            {/each}
          </div>
        </div>
      {/each}
      <div class="out-row">
        <button class="add-v" class:off={!vReady} onclick={() => addVdev('recording')}
          use:hint={() => t(vReady ? 'Add a Windows recording device (loopback) on a virtual channel' : 'Turn on virtual routing in Settings first.')}>+ {t('Recording device')}</button>
      </div>
    </div>
    {/if}
  </div>
</div>

{#if app.status.virtualPending || vrNote}
  <!-- Virtual devices only change when the E2x2 restarts: edits wait here until applied. -->
  <div class="vbar" role="status" class:note={!app.status.virtualPending}>
    {#if app.status.virtualPending}
      <span>{t('Virtual device changes are waiting. Applying restarts the E2x2 (sound stops for a moment).')}</span>
      <button class="btn" onclick={revertV}>{t('Undo changes')}</button>
      <button class="btn primary" disabled={!vReady || !!app.status.virtualRoutingBusy} onclick={applyV}>
        {app.status.virtualRoutingBusy ? t('Applying…') : t('Apply')}
      </button>
    {:else}
      <span>{vrNote}</span>
      <button class="btn" onclick={() => (vrNoteHidden = app.result)}>{t('Close')}</button>
    {/if}
  </div>
{/if}

  <div class="zoom" role="group" aria-label="Zoom">
    <button onclick={() => setZoom(zoom - 0.1)} aria-label="Zoom out">−</button>
    <button class="pct" onclick={() => setZoom(1)} use:hint={() => t('Wheel: zoom · drag the background: move')}>{Math.round(zoom * 100)}%</button>
    <button onclick={() => setZoom(zoom + 0.1)} aria-label="Zoom in">+</button>
    <button class="fit" onclick={fit} use:hint={() => t('Fit to window')}>⤢</button>
  </div>
</div>

{#if ctx}
  <ContextMenu {...ctx} onclose={() => (ctx = null)} />
{/if}
{#if outPopover}
  <OutputPopover {...outPopover} onclose={() => (outPopover = null)} />
{/if}
{#if popover}
  <SendPopover {...popover} onclose={() => (popover = null)} />
{/if}

<style>
  .wrap { position: relative; height: 100%; }
  .routing { position: relative; height: 100%; overflow: auto; }
  .wires { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; overflow: hidden; }
  .wire path { fill: none; stroke: var(--c); stroke-linecap: round; }
  .wire .base { opacity: 0.28; }
  .wire .glow { transition: opacity var(--meter-ms, 80ms) linear; }
  .wire.dashed path { stroke-dasharray: 4 4; }
  .wire.silent .base { opacity: 0.12; }
  .wire .hit { stroke: transparent; stroke-width: 12; pointer-events: stroke; cursor: pointer; }
  .wire:hover .base { opacity: 0.7; }
  .drag { fill: none; stroke: var(--text-2); stroke-width: 1.5; stroke-dasharray: 3 3; }

  .grid {
    position: relative;
    display: grid;
    grid-template-columns: 248px minmax(170px, 210px) minmax(70px, 1fr) minmax(200px, 230px) minmax(70px, 1fr) minmax(190px, 220px) minmax(36px, 70px) 160px;
    grid-template-rows: auto auto auto auto 1fr;
    padding: 0 20px 56px;
    min-height: 100%;
    pointer-events: none;
  }
  .grid > :not(.wires) { pointer-events: auto; }
  /* One opaque header across the whole width, so nothing shows between the column titles when scrolled. */
  .headrow {
    grid-column: 1 / -1; grid-row: 1; display: grid; grid-template-columns: subgrid;
    position: sticky; top: 0; z-index: 3; background: var(--bg); margin: 0 -20px; padding: 0 20px;
    border-bottom: 1px solid var(--line);
  }
  .colhead { padding: 12px 0 10px; display: flex; flex-direction: column; gap: 2px; }
  .zone { font-size: 10.5px; font-weight: 600; letter-spacing: 0.04em; color: var(--text-2); }
  .band { grid-row: 2; align-self: stretch; border-radius: 10px; pointer-events: none !important; margin: 10px -10px 0; }
  .band.win-l { grid-column: 1; grid-row: 2 / 4; background: color-mix(in srgb, var(--mix-c) 4%, transparent); margin-right: 26px; }
  .band.dev { grid-column: 2 / 7; background: color-mix(in srgb, var(--text) 2.5%, transparent); }
  .band.win-r { grid-column: 8; grid-row: 2 / 4; background: color-mix(in srgb, var(--mix-c) 4%, transparent); margin-left: -4px; }
  /* Other Windows outputs are not part of the E2x2: their own zone under it, in the Windows tint. */
  .band.other { grid-column: 2; grid-row: 3; background: color-mix(in srgb, var(--mix-c) 4%, transparent); margin: 14px -10px 0; }
  .band.virt { grid-column: 1 / -1; grid-row: 4; background: color-mix(in srgb, var(--virt) 5%, transparent); margin: 22px -10px 0; }
  .vzone-l { grid-column: 1 / 3; grid-row: 4; display: flex; flex-direction: column; gap: 10px; padding-top: 30px; }
  .vzone-r { grid-column: 6 / 9; grid-row: 4; display: grid; grid-template-columns: subgrid; align-content: start; row-gap: 10px; padding-top: 64px; }
  .vzone-r > .out-row { grid-column: 1 / 4; }
  .zone.vz { color: var(--virt); }
  .others { grid-column: 1 / 3; grid-row: 3; display: flex; flex-direction: column; gap: 10px; padding-top: 18px; }
  .group-h.other-h { padding-top: 4px; }
  .routing.panning { cursor: grabbing; }
  .apps-h { grid-column: 1; }
  .src-h { grid-column: 2; }
  .mix-h { grid-column: 4; }
  .out-h { grid-column: 6; }
  .rec-h { grid-column: 8; }

  .lanes { grid-column: 1 / 3; grid-row: 2; display: flex; flex-direction: column; gap: 10px; padding-top: 10px; }
  .lane { display: grid; grid-template-columns: 248px minmax(0, 1fr); align-items: center; min-height: 56px; }
  .lane > .node { min-width: 0; }
  .group-h { margin-top: 6px; padding-left: 248px; }
  .group-h.out-g { grid-column: 1 / 4; padding-left: 0; margin-top: 4px; }
  .apps { display: flex; flex-direction: column; gap: 4px; padding-right: 36px; }
  .mixes { grid-row: 2; grid-column: 4; display: flex; flex-direction: column; justify-content: center; gap: 14px; }
  .outputs { grid-row: 2; grid-column: 6 / 9; display: grid; grid-template-columns: subgrid; align-content: center; row-gap: 10px; }
  .out-row { display: grid; grid-template-columns: subgrid; grid-column: 1 / 4; align-items: center; }
  .out-row > .node { grid-column: 1; min-width: 0; }
  .recs { grid-column: 3; display: flex; flex-direction: column; gap: 4px; }
  .recby { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 2px; }
  .recapp {
    display: inline-flex; align-items: center; gap: 4px; font-size: 10.5px; color: var(--text-2);
    padding: 1px 6px 1px 3px; border-radius: 3px; background: var(--raised);
  }
  .recapp img { width: 12px; height: 12px; }
  .recapp.idle { opacity: 0.5; }
  /* Windows' own components: shown for completeness, but quiet. */
  .recapp.svc, .app.svc { opacity: 0.55; font-style: italic; }

  .app {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 0;
    padding: 5px 8px 3px;
    border-radius: 5px;
    background: var(--panel);
    border: 1px solid var(--line);
  }
  .app-top { display: grid; grid-template-columns: 18px minmax(0, 1fr) auto auto; align-items: center; column-gap: 8px; min-height: 20px; }
  .app img, .ini { width: 18px; height: 18px; }
  .ini {
    display: grid; place-items: center; border-radius: 4px; background: var(--raised);
    font-size: 8.5px; font-weight: 600; color: var(--text-3);
  }
  .app-name { font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .app-kind { flex: none; font-size: 10.5px; padding: 0 4px; border-radius: 3px; color: var(--text-2); background: var(--hover); }
  .app.idle { opacity: 0.5; }
  .app.muted .app-name { text-decoration: line-through; color: var(--text-3); }
  .app-level {
    position: absolute; left: 4px; bottom: 0; height: 2px; max-width: calc(100% - 8px);
    width: 100%; background: var(--meter-lo);
    /* clip-path, not transform: an animated transform moves the 2px line to its own layer, which snaps to
       the pixel grid differently while moving, so its height flickered. */
    clip-path: inset(0 calc((1 - var(--p)) * 100%) 0 0); transition: clip-path var(--meter-ms, 33ms) linear;
  }
  .asio-app { padding-bottom: 6px; }

  .node {
    position: relative;
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 9px 12px 10px;
    display: flex;
    flex-direction: column;
    gap: 4px;
    transition: border-color 100ms;
  }
  /* While dragging a wire: valid targets get a dashed outline, everything else steps back. */
  .dragging .node, .dragging .app { transition: opacity 120ms, border-color 120ms; }
  .dragging .node:not(.drop):not(.origin), .dragging .app:not(.origin) { opacity: 0.22; }
  .node.drop { outline: 1px dashed var(--text-2); outline-offset: 3px; }
  .node.drop.target { outline: 1.5px solid var(--text); background: var(--raised); }
  .dragging .wire { opacity: 0.4; }
  .n-top { display: flex; align-items: center; gap: 7px; min-width: 0; }
  .n-name { flex: 1; min-width: 0; font-weight: 600; display: flex; align-items: center; }
  .plain { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .n-sub { font-size: 11px; color: var(--text-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .badge { font-size: 9.5px; color: var(--text-3); border: 1px solid var(--line-strong); border-radius: 3px; padding: 0 4px; }
  .act { height: 2px; border-radius: 1px; background: var(--raised); position: relative; overflow: hidden; }
  .act::after {
    content: ''; position: absolute; inset: 0; clip-path: inset(0 calc((1 - var(--p)) * 100%) 0 0); transition: clip-path var(--meter-ms, 33ms) linear;
    background: var(--meter-lo);
  }
  .chan { position: relative; display: grid; grid-template-columns: 30px 1fr; align-items: center; gap: 6px; padding-top: 4px; border-top: 1px solid var(--line); }
  .chan-label { font-size: 10.5px; color: var(--text-3); }
  .chan-body { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .swatch { width: 8px; height: 8px; border-radius: 2px; background: var(--c); flex: none; }
  .mix { border-left: 2px solid var(--c); }
  .node.virt { border-style: dashed; border-color: color-mix(in srgb, var(--virt) 55%, var(--line)); }
  .vbadge { color: var(--virt); border-color: color-mix(in srgb, var(--virt) 50%, transparent); }
  .add-v {
    grid-column: 2; justify-self: stretch; height: 30px; border: 1px dashed var(--line); border-radius: 8px;
    color: var(--text-3); font-size: 12px;
  }
  .out-row > .add-v { grid-column: 1; }
  .add-v:hover { color: var(--text); border-color: var(--virt); background: var(--hover); }
  .add-v.off { opacity: 0.45; }
  .vbar {
    position: absolute; left: 50%; top: 12px; transform: translateX(-50%); z-index: 20;
    display: flex; align-items: center; gap: 10px; padding: 8px 10px 8px 14px; border-radius: 8px;
    background: var(--raised); border: 1px solid color-mix(in srgb, var(--virt) 60%, var(--line-strong));
    box-shadow: 0 8px 24px var(--shadow); font-size: 12px;
  }
  .vbar .btn { height: 26px; padding: 0 10px; border-radius: 5px; border: 1px solid var(--line-strong); font-size: 12px; color: var(--text-2); }
  .vbar .btn.primary { border-color: var(--virt); color: var(--text); }
  .vbar .btn:disabled { opacity: 0.4; }
  .vbar.note { border-color: color-mix(in srgb, var(--warn) 55%, var(--line-strong)); }
  .mix.clip { box-shadow: 0 0 0 1px color-mix(in srgb, var(--warn) 70%, transparent); }
  .clipbadge { font-size: 10px; color: var(--warn); border: 1px solid color-mix(in srgb, var(--warn) 50%, transparent); border-radius: 3px; padding: 0 4px; }
  .grab { cursor: grab; }
  .send.moving { opacity: 0.5; }
  .send.over { box-shadow: 0 -2px 0 var(--text-2); }
  .fold-sends { display: flex; align-items: center; gap: 4px; font-size: 10.5px; color: var(--text-3); padding: 1px 0; align-self: flex-start; }
  .fold-sends:hover { color: var(--text); }
  .chev { display: inline-block; width: 8px; transition: transform 120ms; }
  .chev.open { transform: rotate(90deg); }
  /* All rows share one set of columns, so S and × line up whatever the name or value. */
  .sends { display: grid; grid-template-columns: 62px minmax(0, 1fr) 18px 18px; column-gap: 3px; row-gap: 1px; }
  .send { position: relative; grid-column: 1 / -1; display: grid; grid-template-columns: subgrid; align-items: center; }
  .send.silenced .send-name { color: var(--text-4); }
  .send-name { font-size: 10.5px; color: var(--text-2); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  /* Where the source's wire lands: on the node's left edge, level with this row. */
  .send-in { position: absolute; left: -13px; top: 50%; width: 1px; height: 1px; }
  .ms.x { font-size: 12px; font-weight: 400; }
  .ms.x:hover { color: var(--warn); }
  .frow { display: grid; grid-template-columns: 30px 1fr; align-items: center; gap: 4px; }
  .flabel { font-size: 10px; color: var(--text-4); font-weight: 600; }
  .ms { display: grid; place-items: center; width: 18px; height: 16px; padding: 0; line-height: 1; border-radius: 3px; font-size: 9.5px; font-weight: 700; color: var(--text-4); }
  .ms:hover { background: var(--hover); color: var(--text-2); }
  .ms.solo { background: color-mix(in srgb, var(--meter-mid) 22%, transparent); color: var(--meter-mid); }
  .listen { display: grid; place-items: center; width: 24px; height: 20px; border-radius: 4px; color: var(--text-4); }
  .listen:hover { color: var(--text-2); background: var(--hover); }
  .listen.on { color: var(--c); }
  .out.muted > :not(.n-top):not(.ring-in) { opacity: 0.5; }
  .lr { display: grid; grid-template-columns: 10px 1fr; align-items: center; gap: 4px; font-size: 10px; color: var(--text-4); }
  .m { width: 18px; height: 16px; border-radius: 3px; font-size: 10px; font-weight: 600; color: var(--text-4); }
  .m:hover { background: var(--hover); color: var(--text-2); }
  .m.on { background: color-mix(in srgb, var(--warn) 18%, transparent); color: var(--warn); }

  .toggles { display: flex; gap: 3px; align-items: center; flex-wrap: wrap; }
  .spacer { flex: 1; }
  .tg {
    height: 18px; padding: 0 5px; border-radius: 3px; font-size: 10px; font-weight: 500;
    border: 1px solid var(--line-strong); color: var(--text-3);
  }
  .tg:hover { color: var(--text); }
  .tg.on { background: var(--text); border-color: var(--text); color: var(--bg); }
  .tg.warn.on { background: var(--warn); border-color: var(--warn); color: var(--on-warn); }
  .tg.mute.on { background: color-mix(in srgb, var(--warn) 18%, transparent); border-color: color-mix(in srgb, var(--warn) 50%, transparent); color: var(--warn); }
  .tg.icon { display: grid; place-items: center; width: 22px; padding: 0; border-color: transparent; }
  .tg.icon.on { background: none; color: var(--text-2); border-color: transparent; }

  /* Ports: filled dot = drag from here; hollow ring = drop here. */
  .dot-out {
    position: absolute;
    right: -6px;
    top: 50%;
    width: 11px;
    height: 11px;
    margin-top: -5.5px;
    border-radius: 50%;
    background: var(--text-3);
    border: 2px solid var(--bg);
    cursor: crosshair;
    z-index: 1;
  }
  .dot-out:hover { background: var(--text); transform: scale(1.2); }
  .chan > .dot-out { right: -18px; }
  .ring-in {
    position: absolute;
    left: -5px;
    top: 50%;
    width: 9px;
    height: 9px;
    margin-top: -4.5px;
    border-radius: 50%;
    background: var(--bg);
    border: 1.5px solid var(--text-4);
    pointer-events: none;
  }
  .dragging .node.drop .ring-in { border-color: var(--text); }
  .ring-in.hot { background: var(--text); border-color: var(--text); }

  .chip {
    display: inline-flex; align-items: center; gap: 3px; height: 18px; padding: 0 5px;
    border-radius: 3px; font-size: 10.5px; color: var(--text-3); white-space: nowrap;
    max-width: 104px; overflow: hidden; text-overflow: ellipsis;
  }
  button.chip:hover { background: var(--hover); color: var(--text); }
  .chip.pinned { color: var(--text-2); }
  .chip.fixed { color: var(--text-4); border: 1px solid var(--line); cursor: help; }
  .caret { font-size: 8px; opacity: 0.7; }
  .hint { font-size: 11px; color: var(--text-4); }

  .zoom {
    position: absolute;
    right: 18px;
    bottom: 14px;
    z-index: 5;
    display: flex;
    gap: 1px;
    padding: 2px;
    border-radius: 6px;
    background: var(--panel);
    border: 1px solid var(--line-strong);
  }
  .zoom button { height: 24px; min-width: 24px; padding: 0 6px; border-radius: 4px; color: var(--text-2); font-size: 13px; }
  .zoom button:hover { background: var(--hover); color: var(--text); }
  .zoom .pct { font-size: 11px; min-width: 44px; }
</style>

<script lang="ts">
  import { app, set } from './store.svelte'
  import { t } from './i18n.svelte'
  import {
    setDeviceSetting, setSampleRate, setAsioBuffer, resetStats, setStartWithWindows, updateFirmware, checkLatest, storeOnDevice,
    virtualRouting, updateApp, choosePluginFile, shareLog,
  } from './bridge'
  import { ask } from './confirm.svelte'
  import { vrMessage } from './vrMessage'
  import type { Hotkey, PresetRule } from './model'

  const device = $derived(app.status.device)
  const driver = $derived(app.windows.driver)
  const asio = $derived(driver?.asio)
  const settings = $derived(app.profile!.settings)
  const latest = $derived(app.status.latest)
  const presets = $derived(app.profile!.presets)

  // "V1.10" < "V1.13": numeric-aware string compare.
  const firmwareOld = $derived(!!(device?.softwareVersion && latest?.firmware
    && device.softwareVersion.localeCompare(latest.firmware, undefined, { numeric: true }) < 0))

  // ---- defaults: changed rows are marked and can be reset -------------------------------
  const setSetting = (k: string, v: unknown) => set({ path: `settings.${k}`, value: v })

  let rateBusy = $state(false)
  $effect(() => { if (app.result?.op === 'sampleRate') rateBusy = false })
  async function changeRate(hz: number) {
    if (!(await ask(t('Audio drops out for a moment while the rate changes.'), t('Change')))) return
    rateBusy = true
    setSampleRate(hz)
  }
  async function changeAsio(size: number, safe: boolean) {
    if (!(await ask(t('Changing the buffer restarts the audio stream (short dropout).'), t('Change')))) return
    setAsioBuffer(size, safe)
  }
  let checking = $state(false)
  $effect(() => { void latest; checking = false })
  let advanced = $state(false)
  const khz = (hz: number) => `${(hz / 1000).toLocaleString()} kHz`

  // ---- shortcuts ---------------------------------------------------------------------------
  const ACTIONS = $derived([
    { id: 'muteInput:0', label: t('Mute IN 1') },
    { id: 'muteInput:2', label: t('Mute IN 2') },
    { id: 'muteInput:1', label: t('Mute Mobile IN') },
    { id: 'muteOutput:out12', label: t('Mute OUT 1+2') },
    { id: 'nextPreset', label: t('Next preset') },
    ...presets.map(p => ({ id: `preset:${p.name}`, label: t('Load preset “{name}”', { name: p.name }) })),
  ])
  let recording = $state<number | null>(null)
  const hotkeys = $derived(settings.hotkeys ?? [])
  const keyText = (h: Hotkey) =>
    h.key ? [h.ctrl && 'Ctrl', h.alt && 'Alt', h.shift && 'Shift', h.win && 'Win', keyName(h.key)].filter(Boolean).join(' + ') : ''
  function keyName(vk: number) {
    if (vk >= 0x70 && vk <= 0x87) return `F${vk - 0x6f}`
    if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5a)) return String.fromCharCode(vk)
    const names: Record<number, string> = { 0x20: 'Space', 0x13: 'Pause', 0x91: 'ScrollLock', 0x2d: 'Insert', 0x24: 'Home', 0x21: 'PageUp', 0x22: 'PageDown', 0x23: 'End' }
    return names[vk] ?? `#${vk}`
  }
  const saveHotkeys = (list: Hotkey[]) => setSetting('hotkeys', list)
  function addHotkey() {
    saveHotkeys([...hotkeys, { action: 'muteInput:0', ctrl: false, alt: false, shift: false, win: false, key: 0 }])
    recording = hotkeys.length
  }
  function capture(e: KeyboardEvent, i: number) {
    if (recording !== i) return
    e.preventDefault()
    if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) return
    if (e.key === 'Escape') { recording = null; return }
    saveHotkeys(hotkeys.map((h, j) => (j === i ? { ...h, ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, key: e.keyCode } : h)))
    recording = null
  }

  // ---- performance -------------------------------------------------------------------------
  // Screen refresh; the E2x2's own levels are fixed at ~15 Hz.
  const RATES = [0, 15, 30, 60]
  const rate = $derived(settings.meterRate ?? 30)

  // ---- virtual routing (Thesycon DSP mixer plugin) ----------------------------------------
  const vr = $derived(app.status.virtualRouting)
  const vrBusy = $derived(!!app.status.virtualRoutingBusy)
  let vrError = $state<string | null>(null)
  $effect(() => {
    if (app.result?.op !== 'virtualRouting') return
    vrError = app.result.ok ? null : vrMessage(app.result.message)
  })
  async function vrInstall() {
    if (!(await ask(
      t('Install the plugin (tusbaudiodsp_mixer.sys {version})?', { version: vr?.bundledVersion ?? '?' }) + '\n\n'
      + t('This adds a kernel driver from another product. If it does not match the E2x2 driver, Windows can crash. The registry is backed up first, and a removal script is saved in the backup folder.'),
      t('Install'), true,
    ))) return
    virtualRouting('install')
  }
  async function vrToggle(on: boolean) {
    if (!(await ask(t('The E2x2 restarts to apply this — sound stops for a moment.'), t(on ? 'Turn on' : 'Turn off')))) return
    virtualRouting(on ? 'enable' : 'disable')
  }
  async function vrRemove() {
    if (!(await ask(t('Remove the plugin? Virtual routing stops; the E2x2 restarts if it was on.'), t('Remove'), true))) return
    virtualRouting('remove')
  }

  // ---- automatic presets -------------------------------------------------------------------
  const rules = $derived(settings.presetRules ?? [])
  const saveRules = (list: PresetRule[]) => setSetting('presetRules', list)
  const runningApps = $derived([...new Set(app.windows.sessions.map(s => s.exe).filter((x): x is string => !!x))].sort())
  const editRule = (i: number, patch: Partial<PresetRule>) => saveRules(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  // ---- firmware ----------------------------------------------------------------------------
  let fwOpen = $state(false)
  let fwDialog = $state<HTMLDialogElement>()
  $effect(() => {
    if (fwOpen && fwDialog && !fwDialog.open) fwDialog.showModal()
    else if (!fwOpen && fwDialog?.open) fwDialog.close()
  })
  const fw = $derived(app.firmware)
  const fwBusy = $derived(!!fw && !['Finished', 'Failed'].includes(fw.state))
</script>

{#snippet toggle(on: boolean | null | undefined, onchange: (v: boolean) => void, label: string)}
  <button class="switch" class:on={!!on} role="switch" aria-checked={!!on} aria-label={label} disabled={on == null} onclick={() => onchange(!on)}>
    <span class="knob"></span>
  </button>
{/snippet}

{#snippet boolRow(k: keyof typeof settings, def: boolean, label: string, note?: string)}
  {@const on = (settings[k] as boolean | undefined) ?? def}
  <div class="row">
    {@render key(label, on !== def, () => setSetting(k, def), note)}
    {@render toggle(on, v => setSetting(k, v), label)}
  </div>
{/snippet}

{#snippet key(label: string, changed: boolean, reset?: () => void, note?: string)}
  <span class="k" class:changed>
    <span class="kl">{label}{#if changed}<span class="dot" title={t('Changed from default')}></span>{/if}
      {#if changed && reset}<button class="reset" onclick={reset}>{t('Reset to default')}</button>{/if}</span>
    {#if note}<span class="note">{note}</span>{/if}
  </span>
{/snippet}

<div class="settings">
  <section>
    <h2>{t('General')}</h2>
    <div class="row">
      {@render key(t('Language'), settings.language !== 'auto', () => setSetting('language', 'auto'))}
      <div class="seg">
        {#each [['auto', t('Automatic')], ['ko', '한국어'], ['en', 'English']] as [v, label] (v)}
          <button class:on={settings.language === v} onclick={() => setSetting('language', v)}>{label}</button>
        {/each}
      </div>
    </div>
    <div class="row">
      {@render key(t('Theme'), (settings.theme ?? 'dark') !== 'dark', () => setSetting('theme', 'dark'))}
      <div class="seg">
        {#each [['dark', t('Dark')], ['light', t('Light')], ['auto', t('Automatic')]] as [v, label] (v)}
          <button class:on={(settings.theme ?? 'dark') === v} onclick={() => setSetting('theme', v)}>{label}</button>
        {/each}
      </div>
    </div>
    {@render boolRow('syncWindowsNames', true, t('Sync names with Windows devices'),
      t('E2x2 sources and loopbacks use the Windows device name; renaming one renames the device (asks for admin permission).'))}
    <div class="row">
      {@render key(t('Interface size'), (settings.uiScale ?? 1) !== 1, () => setSetting('uiScale', 1))}
      <div class="seg">
        {#each [0.9, 1, 1.1, 1.25, 1.5] as z (z)}
          <button class:on={(settings.uiScale ?? 1) === z} onclick={() => setSetting('uiScale', z)}>{Math.round(z * 100)}%</button>
        {/each}
      </div>
    </div>
    {@render boolRow('closeToTray', true, t('Keep running in the tray when closed'))}
    <div class="row">
      {@render key(t('Start with Windows'), !!app.status.startWithWindows, () => setStartWithWindows(false), t('Starts in the tray.'))}
      {@render toggle(app.status.startWithWindows ?? false, v => setStartWithWindows(v), t('Start with Windows'))}
    </div>
    {#if app.status.controlCenterAutostart}
      <p class="warnline">{t('TOPPING Control Center also starts with Windows, and TPConsole waits while it runs — turn it off in Task Manager → Startup apps.')}</p>
    {/if}
    {@render boolRow('hideServiceSessions', false, t('Hide Windows service sessions'),
      t('Windows components (svchost) that open audio, such as the Bluetooth or speech services.'))}
    {@render boolRow('miniOnTop', true, t('Mini mode stays on top'))}
    <div class="row">
      {@render key(t('Getting around'), false)}
      <button class="btn" onclick={() => setSetting('hintsSeen', false)}>{t('Show tips again')}</button>
    </div>
  </section>

  <section>
    <h2>{t('Shortcuts')} <span class="dim">{t('Global shortcuts work while other apps (games) are focused.')}</span></h2>
    {#each hotkeys as h, i (i)}
      <div class="row hk">
        <select value={h.action} onchange={e => saveHotkeys(hotkeys.map((x, j) => (j === i ? { ...x, action: (e.target as HTMLSelectElement).value } : x)))}>
          {#each ACTIONS as a (a.id)}<option value={a.id}>{a.label}</option>{/each}
        </select>
        <button class="keycap" class:rec={recording === i} onclick={() => (recording = i)} onkeydown={e => capture(e, i)} onblur={() => recording === i && (recording = null)}>
          {recording === i ? t('Press keys…') : keyText(h) || t('Press keys…')}
        </button>
        <button class="btn ghost" onclick={() => saveHotkeys(hotkeys.filter((_, j) => j !== i))}>{t('Remove')}</button>
      </div>
    {/each}
    {#if app.result?.op === 'hotkeys' && !app.result.ok}<p class="err">{t('Already used by another app')}: {app.result.message}</p>{/if}
    <div class="row"><span></span><button class="btn" onclick={addHotkey}>{t('Add shortcut')}</button></div>
  </section>

  {#if app.status.update}
  {@const u = app.status.update}
  <section>
    <h2>{t('Updates')} <span class="dim">{t('New versions come from GitHub releases ({source}).', { source: u.source })}</span></h2>
    <div class="row">
      {@render key(t('Version'), false)}
      <span class="v">
        {u.current}
        {#if u.available}<span class="new">{t('{v} ready', { v: u.available })}{#if u.failed} · {t('last install failed')}{/if}</span>{/if}
        {#if u.installed}
          <button class="btn" onclick={() => updateApp('check')}>{t('Check now')}</button>
          {#if u.available}<button class="btn" disabled={u.updating} onclick={() => updateApp('apply')}>{u.updating ? t('Downloading…') : t('Update and restart')}</button>{/if}
        {/if}
      </span>
    </div>
    {#if !u.installed}
      <p class="note pad">{t('This copy is not the installed one (development build), so it does not update itself.')}</p>
    {:else}
      {@render boolRow('autoUpdate', true, t('Update automatically'),
        t('Checks GitHub hourly; installs a newer release and restarts TPConsole. Waits while you are using its window.'))}
    {/if}
    {#if u.error}<p class="note pad">{t('Could not check for updates')}: {u.error}</p>{/if}
    {#if app.result?.op === 'update' && !app.result.ok}<p class="err">{app.result.message}</p>{/if}
  </section>
  {/if}

  <section>
    <h2>{t('Performance')} <span class="dim">{t('TPConsole stops all meters and its window while it is in the tray.')}</span></h2>
    <div class="row">
      {@render key(t('Screen refresh'), rate !== 30, () => setSetting('meterRate', 30), t('How often meters and wire glow are redrawn. The E2x2 sends its levels 15 times a second either way. Lower = less CPU.'))}
      <div class="seg">
        {#each RATES as r (r)}
          <button class:on={rate === r} onclick={() => setSetting('meterRate', r)}>{r === 0 ? t('Off') : `${r} fps`}</button>
        {/each}
      </div>
    </div>
    <div class="row">
      {@render key(t('TPConsole right now'), false, undefined, t('Its own process and the window (WebView2), as Task Manager counts it: % of the whole CPU.'))}
      <span class="v">{app.perf ? `CPU ${app.perf.cpu.toFixed(2)}% · ${app.perf.memMb} MB` : '—'}</span>
    </div>
  </section>

  <section>
    <h2>{t('Automatic presets')} <span class="dim">{t('Load a preset while an app runs.')}</span></h2>
    {#if presets.length === 0}
      <p class="dim pad">{t('Save a preset first (top left).')}</p>
    {/if}
    {#each rules as r, i (i)}
      <div class="row hk rule">
        <input class="app" list="running-apps" value={r.app} placeholder={t('App (process name)')}
          onchange={e => editRule(i, { app: (e.target as HTMLInputElement).value.trim().replace(/\.exe$/i, '') })} />
        <span class="arrow">→</span>
        <select value={r.preset} onchange={e => editRule(i, { preset: (e.target as HTMLSelectElement).value })}>
          {#each presets as p (p.name)}<option value={p.name}>{p.name}</option>{/each}
        </select>
        <label class="chk"><input type="checkbox" checked={r.revert} onchange={e => editRule(i, { revert: (e.target as HTMLInputElement).checked })} /> {t('Back when it closes')}</label>
        <button class="btn ghost" onclick={() => saveRules(rules.filter((_, j) => j !== i))}>{t('Remove')}</button>
      </div>
    {/each}
    <datalist id="running-apps">{#each runningApps as a (a)}<option value={a}></option>{/each}</datalist>
    {#if presets.length}
      <div class="row"><span></span><button class="btn" onclick={() => saveRules([...rules, { app: '', preset: presets[0].name, revert: true }])}>{t('Add rule')}</button></div>
    {/if}
  </section>

  <section>
    <h2>{t('Virtual routing')} <span class="dim">{t('Experimental')}</span></h2>
    <div class="row">
      {@render key(t('Use virtual routing'), false, undefined,
        t('Thesycon’s DSP mixer plugin (tusbaudiodsp_mixer.sys; must match the E2x2 driver version). It lets the driver’s virtual channels carry sound, for extra Windows playback / recording devices. TPConsole does not include it: choose the file once.'))}
      <div class="vr">
        {#if vrBusy}
          <span class="dim">{t('Applying…')}</span>
        {:else if !vr}
          <span class="dim">—</span>
        {:else if vr.removing}
          <span class="dim">{t('Removal finishes after re-plugging the E2x2 or restarting Windows')}</span>
        {:else if !vr.installed}
          {#if !vr.bundledVersion}
            <span class="dim">{t('No plugin file yet')}</span>
          {:else if vr.bundledCompatible}
            <span class="dim">{t('Plugin file {v}', { v: vr.bundledVersion })}</span>
            <button class="btn" onclick={vrInstall}>{t('Install')}</button>
          {:else}
            <span class="dim">{t('Plugin file {have} does not match driver {need}', { have: vr.bundledVersion, need: vr.driverVersion ?? '?' })}</span>
          {/if}
          <button class="btn ghost" onclick={choosePluginFile}>{t('Choose file…')}</button>
        {:else}
          <span class="state" class:ok={vr.enabled && vr.loaded} class:bad={vr.enabled && !vr.loaded}>
            {vr.enabled ? (vr.loaded ? t('Running') : t('On — replug the E2x2')) : vr.partial ? t('Partly set') : t('Off')}
          </span>
          {@render toggle(vr.enabled, v => vrToggle(v), t('Use virtual routing'))}
          <button class="btn ghost" onclick={vrRemove}>{t('Remove')}</button>
        {/if}
      </div>
    </div>
    {#if vr?.installed && vr.compatible === false}
      <p class="err">{t('This plugin is from another driver release ({have}); the E2x2 driver {need} ignores it, so routing does nothing. Remove it and install a {need} plugin.', { have: vr.version ?? '?', need: vr.driverVersion ?? '?' })}</p>
    {/if}
    {#if vr?.installed}
      <p class="note pad">tusbaudiodsp_mixer.sys {vr.version ?? ''} · {t('Add virtual devices in the Routing tab (bottom of the graph).')}</p>
    {/if}
    {#if vrError}<p class="err">{vrError}</p>{/if}
    {#if app.result?.op === 'pluginFile' && !app.result.ok}<p class="err">{app.result.message}</p>{/if}
  </section>

  <section>
    <h2>{t('Device')} <span class="dim">E2x2 OTG</span></h2>
    <div class="row">
      {@render key(t('Firmware'), false)}
      <span class="v">
        <span>{device?.softwareVersion ?? t('Unknown')}</span>
        {#if latest?.firmware && device?.softwareVersion}
          {#if firmwareOld}
            <span class="new">{t('{v} available — update with TOPPING Control Center', { v: latest.firmware })}</span>
          {:else}
            <span class="ok">{t('Up to date (latest {v})', { v: latest.firmware })}</span>
          {/if}
        {/if}
        {#if firmwareOld}
          <button class="btn" onclick={() => (fwOpen = true)}>{t('Update firmware…')}</button>
        {:else}
          <button class="btn ghost" disabled={checking} onclick={() => { checking = true; checkLatest() }}>{t(checking ? 'Checking…' : 'Check for updates')}</button>
        {/if}
      </span>
    </div>
    <div class="row">
      {@render key(t('Hardware'), false, undefined, t('Board revision; not updatable.'))}
      <span class="v">{device?.hardwareVersion ?? t('Unknown')}</span>
    </div>
    <div class="row">
      {@render key(t('Sample rate'), !!driver && driver.sampleRate !== 48000, () => changeRate(48000))}
      <div class="seg">
        {#each driver?.sampleRates ?? [] as hz (hz)}
          <button class:on={driver?.sampleRate === hz} disabled={rateBusy} onclick={() => driver?.sampleRate !== hz && changeRate(hz)}>{khz(hz)}</button>
        {/each}
      </div>
    </div>
    {#if app.result?.op === 'sampleRate' && !app.result.ok}<p class="err">{app.result.message}</p>{/if}
    <div class="row">
      {@render key(t('ASIO buffer'), false, undefined, asio ? (app.windows.asio?.hosts.map(h => h.name).join(', ') ?? '') : t('Not in use'))}
      {#if asio}
        <div class="seg">
          {#each asio.bufferSizes as size (size)}
            <button class:on={asio.bufferSize === size} onclick={() => asio.bufferSize !== size && changeAsio(size, asio.safeMode)}>{size}</button>
          {/each}
        </div>
      {/if}
    </div>
    {#if asio}
      <div class="row">
        {@render key(t('Safe mode'), false)}
        {@render toggle(asio.safeMode, v => changeAsio(asio.bufferSize, v), t('Safe mode'))}
      </div>
    {/if}
    {#if app.result?.op === 'asioBuffer' && !app.result.ok}<p class="err">{app.result.message}</p>{/if}
    <div class="row">
      {@render key(t('Brightness'), device?.brightness != null && device.brightness !== 1, () => setDeviceSetting(4, 1))}
      <div class="seg">
        {#each [t('Dim'), t('Normal'), t('Bright')] as label, i (i)}
          <button class:on={device?.brightness === i} disabled={device?.brightness == null} onclick={() => setDeviceSetting(4, i)}>{label}</button>
        {/each}
      </div>
    </div>
    <div class="row">
      {@render key(t('Automatic standby'), device?.autoStandby === true, () => setDeviceSetting(2, 0))}
      {@render toggle(device?.autoStandby, v => setDeviceSetting(2, v ? 1 : 0), t('Automatic standby'))}
    </div>
    <div class="row">
      {@render key(t('Mobile app function'), device?.mobileApp === true, () => setDeviceSetting(3, 0), t('Takes effect after the device restarts.'))}
      {@render toggle(device?.mobileApp, v => setDeviceSetting(3, v ? 1 : 0), t('Mobile app function'))}
    </div>
    <div class="row">
      {@render key(t('Keep the E2x2 up to date for use without a PC'), (settings.autoStoreOnDevice ?? true) !== true,
        () => setSetting('autoStoreOnDevice', true),
        app.status.storedUpToDate ? t('The E2x2 has the current setup.') : t('Saved to the E2x2 when TPConsole closes (like Control Center), or now with Save now.'))}
      <span class="v">
        {#if !app.status.storedUpToDate}<button class="btn ghost" onclick={storeOnDevice}>{t('Save now')}</button>{/if}
        {@render toggle(settings.autoStoreOnDevice ?? true, v => setSetting('autoStoreOnDevice', v), t('Keep the E2x2 up to date for use without a PC'))}
      </span>
    </div>
  </section>

  <section>
    <button class="fold" aria-expanded={advanced} onclick={() => (advanced = !advanced)}>
      <span class="chev" class:open={advanced}>›</span>{t('Advanced')}<span class="dim">{t('Driver, ASIO and diagnostics')}</span>
    </button>
    {#if advanced}
    <div class="row">
      {@render key(t('Driver'), false)}
      <span class="v">{driver?.version ?? t('Unknown')}</span>
    </div>
      <h3 class="sub">{t('Diagnostics')} <span class="dim">{t('Counts since the driver started or the last reset.')}</span></h3>
      <div class="row">
        {@render key(t('Problem report'), false, undefined, t('Logs and versions in one file. Copy it, then paste (Ctrl+V) into Discord, a chat or an email to attach it. Your Windows user folder is hidden.'))}
        <span class="btns">
          <button class="btn" onclick={() => shareLog('copy')}>{t('Copy log file')}</button>
          <button class="btn ghost" onclick={() => shareLog('folder')}>{t('Show in folder')}</button>
        </span>
      </div>
      {#if app.result?.op === 'shareLog'}
        <p class={app.result.ok ? 'note pad' : 'err'}>{app.result.ok ? t('Copied. Paste it with Ctrl+V where you want to send it.') : app.result.message}</p>
      {/if}
    {#if driver?.stats}
      <div class="row">
        {@render key(t('Audio dropouts'), false)}
        <span class="v">{driver.stats.dropouts.toLocaleString()}{#if driver.stats.recentDropouts}<span class="new">+{driver.stats.recentDropouts} / 1 min</span>{/if}</span>
      </div>
      <div class="row">
        {@render key(t('USB errors'), false)}
        <span class="v">{driver.stats.usbErrors.toLocaleString()}{#if driver.stats.recentUsbErrors}<span class="new">+{driver.stats.recentUsbErrors} / 1 min</span>{/if}</span>
      </div>
      <div class="row"><span></span><button class="btn" onclick={resetStats}>{t('Reset counters')}</button></div>
    {/if}
    {#if driver?.events?.length}
      <pre class="log">{driver.events.join('\n')}</pre>
    {/if}
    {/if}
  </section>
</div>

<!-- Native modal; it cannot be dismissed while an update runs. -->
<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
<dialog class="modal" bind:this={fwDialog} aria-label={t('Firmware update')}
  oncancel={e => { e.preventDefault(); if (!fwBusy) fwOpen = false }} onclick={e => e.target === fwDialog && !fwBusy && (fwOpen = false)}>
  {#if fwOpen}
      <h3>{t('Firmware update')}</h3>
      <p class="warnbox">{t('Recommended: update with the official TOPPING Control Center. TPConsole follows the same procedure, but a failed update can make the device unusable until it is recovered.')}</p>
      <p class="dim">{t('Do not unplug the E2x2 or close TPConsole until it finishes.')}</p>
      {#if fw}
        <div class="progress"><div style:width="{fw.progress}%"></div></div>
        <p class:err={fw.state === 'Failed'} class:ok={fw.state === 'Finished'}>
          {fw.state === 'Finished' ? t('Update finished. The device restarts.')
            : fw.state === 'Failed' ? t('Update failed: {m}', { m: fw.message ?? '' })
            : t('Updating… {p}%', { p: fw.progress })}
        </p>
      {/if}
      <div class="actions">
        <button class="btn ghost" disabled={fwBusy} onclick={() => { fwOpen = false; app.firmware = null }}>{t('Close')}</button>
        <button class="btn" disabled={fwBusy} onclick={() => updateFirmware('pick')}>{t('Choose a .bin file…')}</button>
        <button class="btn danger" disabled={fwBusy || !latest?.firmware} onclick={() => updateFirmware('download')}>
          {t('Download and install {v}', { v: latest?.firmware ?? '' })}
        </button>
      </div>
  {/if}
</dialog>

<style>
  .vr { display: flex; align-items: center; gap: 10px; }
  .state { font-size: 12px; color: var(--text-3); }
  .state.ok { color: var(--meter-lo); }
  .state.bad { color: var(--warn); }
  .rule { grid-template-columns: 1fr auto 1fr auto auto; }
  .rule .app { min-width: 0; }
  .arrow { color: var(--text-4); }
  .chk { display: flex; align-items: center; gap: 5px; font-size: 12px; color: var(--text-3); white-space: nowrap; }
  .chk input { width: auto; margin: 0; }
  .pad { padding: 4px 0; }
  .settings { height: 100%; overflow: auto; padding: 28px 32px 48px; display: flex; flex-direction: column; gap: 32px; }
  section { width: 100%; max-width: 760px; margin: 0 auto; display: flex; flex-direction: column; gap: 2px; }
  h2 { margin: 0 0 8px; font-size: 13px; font-weight: 600; letter-spacing: 0.02em; display: flex; gap: 10px; align-items: baseline; }
  .dim { color: var(--text-3); font-weight: 400; font-size: 12px; }
  .row { display: flex; align-items: center; justify-content: space-between; gap: 16px; min-height: 40px; border-bottom: 1px solid var(--line); }
  .k { color: var(--text-2); display: flex; flex-direction: column; min-width: 0; }
  .kl { display: flex; align-items: center; gap: 6px; }
  .k.changed .kl { color: var(--text); font-weight: 600; }
  .dot { width: 5px; height: 5px; border-radius: 50%; background: var(--meter-mid); }
  .reset { font-size: 11px; font-weight: 400; color: var(--text-3); padding: 1px 6px; border-radius: 3px; }
  .reset:hover { color: var(--text); background: var(--hover); }
  .note { font-size: 11px; color: var(--text-4); max-width: 420px; }
  .v { color: var(--text); display: flex; align-items: baseline; gap: 10px; }
  .ok { font-size: 11.5px; color: var(--meter-lo); }
  .new { font-size: 11.5px; color: var(--meter-mid); }
  .btns { display: flex; gap: 6px; }
  .err { color: var(--warn); font-size: 12px; margin: 4px 0; }
  .seg { display: flex; gap: 2px; padding: 2px; border-radius: 6px; background: var(--panel); border: 1px solid var(--line); flex-wrap: wrap; justify-content: flex-end; }
  .seg button { height: 26px; padding: 0 10px; border-radius: 4px; font-size: 12px; color: var(--text-3); }
  .seg button:hover:not(:disabled) { color: var(--text); }
  .seg button.on { background: var(--raised); color: var(--text); }
  .seg button:disabled { opacity: 0.5; cursor: default; }
  .switch { width: 34px; height: 20px; border-radius: 10px; background: var(--line-strong); position: relative; transition: background 120ms; flex: none; }
  .switch .knob { position: absolute; top: 3px; left: 3px; width: 14px; height: 14px; border-radius: 50%; background: var(--text-2); transition: transform 120ms; }
  .switch.on { background: var(--meter-lo); }
  .switch.on .knob { transform: translateX(14px); background: var(--bg); }
  .switch:disabled { opacity: 0.4; }
  .btn { height: 28px; padding: 0 12px; border-radius: 5px; border: 1px solid var(--line-strong); font-size: 12px; color: var(--text-2); }
  .btn:hover:not(:disabled) { color: var(--text); border-color: var(--text-3); }
  .btn:disabled { opacity: 0.4; cursor: default; }
  .btn.ghost { border-color: transparent; }
  .btn.danger { border-color: color-mix(in srgb, var(--warn) 60%, transparent); color: var(--warn); }
  .hk { gap: 10px; justify-content: flex-start; }
  .hk select { height: 28px; flex: 1; min-width: 0; background: var(--panel); border: 1px solid var(--line-strong); border-radius: 5px; padding: 0 6px; }
  .keycap { height: 28px; min-width: 150px; padding: 0 10px; border-radius: 5px; border: 1px solid var(--line-strong); background: var(--bg); font-size: 12px; }
  .keycap.rec { border-color: var(--meter-mid); color: var(--meter-mid); }
  .warnline { margin: 6px 0 0; padding: 8px 10px; border-radius: 6px; font-size: 12px; color: var(--text);
    background: color-mix(in srgb, var(--meter-mid) 12%, transparent); }
  .fold { display: flex; align-items: baseline; gap: 8px; font-size: 13px; font-weight: 600; padding: 4px 0 10px; }
  .fold .dim { font-weight: 400; }
  .chev { display: inline-block; width: 10px; transition: transform 120ms; color: var(--text-3); }
  .chev.open { transform: rotate(90deg); }
  h3.sub { margin: 18px 0 6px; font-size: 12.5px; font-weight: 600; display: flex; gap: 10px; align-items: baseline; }
  .log { margin: 10px 0 0; padding: 8px 10px; border-radius: 6px; background: var(--panel); border: 1px solid var(--line); font-size: 11px; color: var(--text-3); white-space: pre-wrap; }

  .modal::backdrop { background: var(--backdrop); }
  .modal { width: 460px; max-width: calc(100vw - 32px); padding: 20px 22px; border-radius: 10px; color: var(--text); background: var(--raised); border: 1px solid var(--line-strong); box-shadow: 0 20px 50px var(--shadow); }
  .modal h3 { margin: 0 0 10px; font-size: 14px; }
  .warnbox { margin: 0 0 8px; padding: 10px 12px; border-radius: 6px; background: color-mix(in srgb, var(--warn) 12%, transparent); color: var(--text); font-size: 12.5px; }
  .progress { height: 4px; border-radius: 2px; background: var(--line-strong); margin: 12px 0 6px; overflow: hidden; }
  .progress div { height: 100%; background: var(--meter-lo); transition: width 200ms; }
  .actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 14px; flex-wrap: wrap; }
</style>

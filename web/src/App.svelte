<script lang="ts">
  import { app, set, undo, redo, history } from './lib/store.svelte'
  import { isMock, isDemo, setMini, closeControlCenter } from './lib/bridge'
  import Mini from './lib/Mini.svelte'
  import { t, lang } from './lib/i18n.svelte'
  import { hint, hintState } from './lib/hint.svelte'
  import Routing from './lib/Routing.svelte'
  import PresetMenu from './lib/PresetMenu.svelte'
  import Settings from './lib/Settings.svelte'
  import Dialog from './lib/Dialog.svelte'

  let tab = $state<'routing' | 'settings'>('routing')

  const knob = $derived(app.status.monitorMixKnob)
  const tipsSeen = $derived(app.profile?.settings?.hintsSeen ?? true)
  $effect(() => { document.documentElement.lang = lang() })
  // Theme (dark by default): app.css colours are light-dark() pairs; "auto" lets WebView2 follow Windows.
  $effect(() => {
    const t = app.profile?.settings?.theme ?? 'dark'
    document.documentElement.style.colorScheme = t === 'auto' ? 'light dark' : t
  })
  // Meter bars glide from one device value to the next: the E2x2 sends levels every ~67 ms (15 Hz),
  // whatever the screen refresh is.
  $effect(() => {
    const r = app.profile?.settings?.meterRate ?? 30
    document.documentElement.style.setProperty('--meter-ms', r > 0 ? '67ms' : '0ms')
  })

  let mini = $state(false)
  function toggleMini(on: boolean) { mini = on; setMini(on) }

  // Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) everywhere except while typing.
  function keys(e: KeyboardEvent) {
    if (!e.ctrlKey || e.altKey) return
    const typing = (e.target as Element)?.closest?.('input, textarea, select, [contenteditable]')
    if (typing) return
    const k = e.key.toLowerCase()
    if (k === 'z' && !e.shiftKey) { e.preventDefault(); undo() }
    else if (k === 'y' || (k === 'z' && e.shiftKey)) { e.preventDefault(); redo() }
  }
</script>

<svelte:window onkeydown={keys} />

{#if mini && app.profile}
  <Mini onexpand={() => toggleMini(false)} />
{:else}

<div class="shell">
  <header class="bar">
    <div class="device">
      <span class="conn" class:ok={app.status.connected}></span>
      <span class="model">E2x2 OTG</span>
      <span class="state">{t(app.status.connected ? 'Connected' : 'Searching for device…')}</span>
      {#if isMock && !isDemo}<span class="chip">{t('Preview · no device')}</span>{/if}
      {#if app.profile}<span class="vsep"></span><PresetMenu />{/if}
    </div>

    <div class="undo">
      <button disabled={!history.canUndo} onclick={undo} aria-label={t('Undo')} use:hint={() => `${t('Undo')} (Ctrl+Z)`}>↶</button>
      <button disabled={!history.canRedo} onclick={redo} aria-label={t('Redo')} use:hint={() => `${t('Redo')} (Ctrl+Y)`}>↷</button>
    </div>

    <nav class="tabs" aria-label="View">
      <button class:on={tab === 'routing'} onclick={() => (tab = 'routing')}>{t('Routing')}</button>
      <button class:on={tab === 'settings'} onclick={() => (tab = 'settings')}>{t('Settings')}</button>
    </nav>

    <div class="right">
      <button class="minib" onclick={() => toggleMini(true)} use:hint={() => t('Mini mode: a small window with presets, volumes and meters')}>▭</button>
      <div class="blend" use:hint={() => t('Front-panel Input / Playback knob (read-only)')}>
        <span class="label">{t('Input')}</span>
        <div class="blend-track">
          {#if knob !== null}<div class="blend-pos" style:left="{knob}%"></div>{/if}
        </div>
        <span class="label">{t('Playback')}</span>
      </div>
    </div>
  </header>

  {#if app.profile}
    {#if tab === 'routing'}
      <div class="view"><Routing /></div>
    {:else}
      <div class="view"><Settings /></div>
    {/if}
  {/if}

  <!-- What can I do with this? The hovered element says so here. -->
  <footer class="hintbar" aria-live="polite">
    <span class="i">?</span>
    <span>{hintState.text ?? t('Hover anything to see what it does. Right-click nodes and wires for all actions.')}</span>
  </footer>

  <Dialog />

  {#if app.status.controlCenterRunning}
    <!-- Two apps driving one device fight each other: TPConsole waits until Control Center is gone. -->
    <div class="ccblock" role="alertdialog" aria-label={t('TOPPING Control Center is running')}>
      <div class="cccard">
        <h3>{t('TOPPING Control Center is running')}</h3>
        <p>{t('Both apps would change the E2x2 at the same time, so TPConsole leaves it alone while Control Center is open. Close Control Center to continue — your setup is sent back right away.')}</p>
        {#if app.status.controlCenterAutostart}<p class="dim">{t('Control Center also starts with Windows. Turn that off in Task Manager → Startup apps.')}</p>{/if}
        <button onclick={closeControlCenter}>{t('Close Control Center')}</button>
        {#if app.result?.op === 'closeControlCenter' && !app.result.ok}<p class="err">{app.result.message}</p>{/if}
      </div>
    </div>
  {/if}

  {#if app.profile && !tipsSeen}
    <div class="tips" role="dialog" aria-label={t('Getting around')}>
      <h3>{t('Getting around')}</h3>
      <ol>
        <li>{t('Hover over anything — the bar at the bottom tells you what you can do with it.')}</li>
        <li>{t('Drag the small dots to connect. Right-click a wire to disconnect.')}</li>
        <li>{t('Right-click any node for every action it has.')}</li>
      </ol>
      <button onclick={() => set({ path: 'settings.hintsSeen', value: true })}>{t('Got it')}</button>
    </div>
  {/if}
</div>
{/if}

<style>
  .shell { display: flex; flex-direction: column; height: 100%; position: relative; }
  .bar {
    flex: none;
    height: 44px;
    padding: 0 16px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    border-bottom: 1px solid var(--line);
  }
  .device { display: flex; align-items: center; gap: 10px; flex: 1; }
  .undo { display: flex; gap: 2px; margin-right: 10px; }
  .undo button, .minib { width: 26px; height: 26px; border-radius: 5px; color: var(--text-3); font-size: 14px; }
  .undo button:hover:not(:disabled), .minib:hover { background: var(--hover); color: var(--text); }
  .undo button:disabled { opacity: 0.3; }
  .vsep { width: 1px; height: 18px; background: var(--line); margin: 0 4px; }
  .tabs { display: flex; gap: 2px; padding: 2px; border-radius: 6px; background: var(--panel); border: 1px solid var(--line); }
  .tabs button { height: 26px; padding: 0 14px; border-radius: 4px; font-size: 12px; color: var(--text-3); }
  .tabs button:hover { color: var(--text-2); }
  .tabs button.on { background: var(--raised); color: var(--text); }
  .view { flex: 1; min-height: 0; }
  .model { font-weight: 600; letter-spacing: 0.01em; }
  .state { color: var(--text-3); font-size: 12px; }
  .right { flex: 1; display: flex; align-items: center; justify-content: flex-end; gap: 16px; }
  .chip {
    font-size: 11px;
    color: var(--text-3);
    border: 1px solid var(--line-strong);
    border-radius: 3px;
    padding: 2px 7px;
  }
  .ccblock { position: absolute; inset: 44px 0 0; z-index: 20; display: grid; place-items: center; background: color-mix(in srgb, var(--bg) 82%, transparent); backdrop-filter: blur(2px); }
  .cccard { width: 440px; max-width: calc(100% - 32px); padding: 22px 24px; border-radius: 10px; background: var(--raised); border: 1px solid var(--line-strong); box-shadow: 0 20px 50px var(--shadow); }
  .cccard h3 { margin: 0 0 10px; font-size: 15px; }
  .cccard p { margin: 0 0 12px; color: var(--text-2); line-height: 1.5; }
  .cccard .dim { color: var(--text-3); font-size: 12px; }
  .cccard .err { color: var(--warn); font-size: 12px; margin: 10px 0 0; }
  .cccard button { height: 32px; padding: 0 16px; border-radius: 6px; background: var(--text); color: var(--bg); font-size: 13px; font-weight: 500; }
  .blend { display: flex; align-items: center; gap: 8px; }
  .blend-track { position: relative; width: 84px; height: 3px; border-radius: 2px; background: var(--line-strong); }
  .blend-pos {
    position: absolute;
    top: -3px;
    width: 3px;
    height: 9px;
    margin-left: -1.5px;
    border-radius: 1px;
    background: var(--text-2);
  }


  .hintbar {
    flex: none;
    height: 28px;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 0 16px;
    border-top: 1px solid var(--line);
    font-size: 11.5px;
    color: var(--text-3);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .hintbar .i {
    display: grid; place-items: center; width: 15px; height: 15px; border-radius: 50%;
    border: 1px solid var(--line-strong); font-size: 9.5px; color: var(--text-3); flex: none;
  }

  .tips {
    position: absolute;
    right: 20px;
    bottom: 44px;
    z-index: 40;
    width: 320px;
    padding: 16px 18px 14px;
    border-radius: 10px;
    background: var(--raised);
    border: 1px solid var(--line-strong);
    box-shadow: 0 16px 40px var(--shadow);
  }
  .tips h3 { margin: 0 0 8px; font-size: 13px; }
  .tips ol { margin: 0 0 12px; padding-left: 18px; display: flex; flex-direction: column; gap: 6px; color: var(--text-2); font-size: 12px; }
  .tips button { height: 26px; padding: 0 12px; border-radius: 5px; background: var(--text); color: var(--bg); font-size: 12px; float: right; }
</style>

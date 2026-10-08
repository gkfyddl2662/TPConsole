<script lang="ts">
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { app } from './store.svelte'
  import { presetAction } from './bridge'
  import { outside } from './outside'

  // One button in the top bar: the current setup's name. Everything about presets lives in its menu.
  const profile = $derived(app.profile!)
  const active = $derived(profile.presets.find(p => p.name === profile.activePreset))
  const edited = $derived(!active || JSON.stringify(active.mixer) !== JSON.stringify(profile.mixer))

  let open = $state(false)
  let naming = $state<'new' | string | null>(null) // 'new' or the preset being renamed
  // Also remember where apps play, device volumes and default devices.
  let withWindows = $state(true)

  function close() {
    open = false
    naming = null
  }


  // "Stored" feedback fades after a moment.
  $effect(() => {
    if (app.stored === null) return
    const t = setTimeout(() => (app.stored = null), 2500)
    return () => clearTimeout(t)
  })

  function submitName(e: Event) {
    e.preventDefault()
    const input = (e.target as HTMLFormElement).elements.namedItem('name') as HTMLInputElement
    const name = input.value.trim()
    if (!name) return
    if (naming === 'new') presetAction('save', name, undefined, withWindows)
    else if (naming && name !== naming) presetAction('rename', naming, name)
    naming = null
  }

  const focus = (node: HTMLInputElement) => { node.focus(); node.select() }
</script>

<svelte:window onkeydown={e => e.key === 'Escape' && open && close()} />

<div class="preset" use:outside={() => open && close()}>
  <button class="current" class:open onclick={() => (open ? close() : (open = true))} aria-haspopup="menu" aria-expanded={open}>
    <span class="label">{t('Preset')}</span>
    <span class="name">{active?.name ?? t('Untitled')}</span>
    {#if edited}<span class="dot" use:hint={() => t('Changed since last saved')}></span>{/if}
    <span class="caret">▾</span>
  </button>
  {#if app.stored !== null}
    <span class="flash" class:bad={!app.stored}>{t(app.stored ? 'Stored on device' : 'Device not connected')}</span>
  {/if}

  {#if open}
    <div class="menu" role="menu">
      {#if profile.presets.length === 0}
        <p class="empty">{t('Save the current setup to switch back to it later.')}</p>
      {/if}
      {#each profile.presets as p (p.name)}
        {#if naming === p.name}
          <form onsubmit={submitName}><input name="name" value={p.name} use:focus onblur={() => (naming = null)} /></form>
        {:else}
          <div class="row" class:on={p.name === profile.activePreset}>
            <button class="pick" role="menuitemradio" aria-checked={p.name === profile.activePreset}
              onclick={() => { presetAction('load', p.name); close() }}>
              <span class="radio"></span>{p.name}
              {#if p.windows}<span class="tag" use:hint={() => t('Also restores where apps play, device volumes and default devices')}>+ Windows</span>{/if}
            </button>
            <button class="icon" aria-label={t('Rename')} onclick={() => (naming = p.name)}>✎</button>
            <button class="icon" aria-label={t('Delete')} onclick={() => presetAction('delete', p.name)}>×</button>
          </div>
        {/if}
      {/each}

      <div class="sep"></div>
      {#if active && edited}
        <button class="item" onclick={() => { presetAction('save', active.name, undefined, withWindows); close() }}>
          {t('Save changes to “{name}”', { name: active.name })}
        </button>
      {/if}
      {#if naming === 'new'}
        <form onsubmit={submitName}><input name="name" placeholder={t('Preset name')} use:focus onblur={() => (naming = null)} /></form>
      {:else}
        <button class="item" onclick={() => (naming = 'new')}>{t('Save as new preset…')}</button>
      {/if}
      <label class="opt" use:hint={() => t('Also restores where apps play, device volumes and default devices')}>
        <input type="checkbox" bind:checked={withWindows} /> {t('Include Windows apps and devices')}
      </label>

    </div>
  {/if}
</div>

<style>
  .preset { position: relative; display: flex; align-items: center; gap: 10px; }
  .current {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 28px;
    padding: 0 10px;
    border-radius: 6px;
    border: 1px solid var(--line);
  }
  .current:hover, .current.open { background: var(--panel); border-color: var(--line-strong); }
  .name { font-weight: 600; max-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dot { width: 6px; height: 6px; border-radius: 50%; background: var(--meter-mid); }
  .caret { font-size: 9px; color: var(--text-3); }
  .flash { font-size: 11px; color: var(--meter-lo); }
  .flash.bad { color: var(--warn); }

  .menu {
    position: absolute;
    top: 34px;
    left: 0;
    z-index: 30;
    width: 280px;
  }
  .empty { margin: 6px 8px; font-size: 12px; color: var(--text-3); }
  .row { display: flex; align-items: center; border-radius: 5px; }
  .row:hover { background: var(--hover); }
  .pick { flex: 1; display: flex; align-items: center; gap: 8px; padding: 6px 8px; text-align: left; min-width: 0; }
  .radio { width: 6px; height: 6px; border-radius: 50%; border: 1px solid var(--text-4); flex: none; }
  .row.on .radio { background: var(--text); border-color: var(--text); }
  .icon { width: 24px; height: 24px; border-radius: 4px; color: var(--text-4); opacity: 0; }
  .row:hover .icon { opacity: 1; }
  .icon:hover { color: var(--text); background: var(--line); }
  .item { width: 100%; text-align: left; padding: 7px 8px; border-radius: 5px; font-size: 12.5px; }
  .item:hover { background: var(--hover); }
  .tag { margin-left: auto; font-size: 10px; color: var(--text-4); }
  .opt { display: flex; align-items: center; gap: 6px; padding: 6px 8px 2px; font-size: 11.5px; color: var(--text-3); }
  .opt input { width: auto; margin: 0; }
  form { padding: 3px 4px; }
  input {
    width: 100%;
    height: 28px;
    padding: 0 8px;
    border-radius: 5px;
    border: 1px solid var(--line-strong);
    background: var(--bg);
    outline: none;
  }
</style>

<script lang="ts">
  import { app, set, nameOf, mixName, outOff, muteOut, mixMeters } from './store.svelte'
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { MIXES, OUTPUTS } from './model'
  import { setAppVolume, setEndpointVolume } from './bridge'
  import { peakPos } from './scale'
  import PresetMenu from './PresetMenu.svelte'
  import Fader from './Fader.svelte'
  import Meter from './Meter.svelte'

  // The essentials in a small, always-at-hand window: preset, outputs, mix meters, app volumes.
  let { onexpand }: { onexpand: () => void } = $props()
  const mixer = $derived(app.profile!.mixer)
  const apps = $derived(
    app.windows.sessions
      .filter(s => s.flow !== 'capture' && s.pid !== 0 && s.active && !s.service)
      .filter((s, i, all) => all.findIndex(x => x.pid === s.pid) === i)
      .sort((a, b) => a.name.localeCompare(b.name)),
  )
  const pbs = $derived(app.windows.endpoints.filter(e => e.e2x2?.startsWith('pb')))
</script>

<div class="mini">
  <header>
    <span class="conn" class:ok={app.status.connected}></span>
    <PresetMenu />
    <button class="exp" onclick={onexpand} use:hint={() => t('Back to the full window')} aria-label={t('Back to the full window')}>⤢</button>
  </header>

  <section>
    <h3>{t('Outputs')}</h3>
    {#each OUTPUTS.filter(d => d.key === 'out12' || d.key === 'mobileOut') as d (d.key)}
      {@const o = mixer[d.key]}
      {@const off = outOff(d.key)}
      <div class="row">
        <span class="lbl">{nameOf(`out:${d.key}`, d.name)}</span>
        <Fader compact value={o.levelDbL} min={-89} max={0} dim={off} color="var(--text-2)" label={d.name}
          onchange={v => set({ path: `mixer.${d.key}.levelDbL`, value: v }, { path: `mixer.${d.key}.levelDbR`, value: v })} />
        <button class="m" class:on={off} onclick={() => muteOut(d.key, !off)}>M</button>
      </div>
    {/each}
  </section>

  <section>
    <h3>{t('Mixes')}</h3>
    <div class="mixes">
      {#each MIXES as mx, m (mx)}
        <div class="mx" style:--c="var(--mix-{mx.toLowerCase()})">
          <span class="lbl">{mixName(m)}</span>
          <Meter values={mixMeters(m)} />
        </div>
      {/each}
    </div>
  </section>

  <section>
    <h3>Windows</h3>
    {#each pbs as ep (ep.id)}
      <div class="row">
        <span class="lbl">{ep.name}</span>
        <Fader compact volume value={ep.volume}
          label={ep.name} dim={ep.muted} onchange={v => setEndpointVolume(ep.id, v ?? 0)} />
        <span class="lvl" style:--p={peakPos(app.winPeaks[ep.id])}></span>
      </div>
    {/each}
  </section>

  <section class="grow">
    <h3>{t('Apps')}</h3>
    {#each apps as s (s.key)}
      <div class="row">
        <span class="lbl app">{#if s.icon}<img src={s.icon} alt="" />{/if}{s.name}</span>
        <Fader compact volume value={s.volume}
          label={s.name} dim={s.muted} onchange={v => setAppVolume(s.key, v ?? 0)} />
        <span class="lvl" style:--p={peakPos(app.winPeaks[s.key])}></span>
      </div>
    {/each}
  </section>
</div>

<style>
  .mini { height: 100%; display: flex; flex-direction: column; overflow: auto; padding: 0 12px 12px; }
  header { display: flex; align-items: center; gap: 8px; height: 44px; flex: none; }
  .exp { margin-left: auto; width: 26px; height: 26px; border-radius: 5px; color: var(--text-3); }
  .exp:hover { background: var(--hover); color: var(--text); }
  section { display: flex; flex-direction: column; gap: 6px; padding: 8px 0; border-top: 1px solid var(--line); }
  h3 { margin: 0; font-size: 11px; font-weight: 500; color: var(--text-3); }
  .row { display: grid; grid-template-columns: 92px 1fr auto; align-items: center; gap: 8px; }
  .lbl { font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .app { display: flex; align-items: center; gap: 6px; }
  .app img { width: 14px; height: 14px; }
  .m { width: 22px; height: 20px; border-radius: 3px; font-size: 10px; color: var(--text-3); border: 1px solid var(--line); }
  .m.on { color: var(--warn); border-color: var(--warn); }
  .lvl { width: 4px; height: 16px; border-radius: 2px; background: linear-gradient(to top, var(--meter-lo) calc(var(--p) * 100%), var(--raised) 0); }
  .mixes { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 12px; }
  .mx { display: flex; flex-direction: column; gap: 3px; border-left: 2px solid var(--c); padding-left: 6px; }
</style>

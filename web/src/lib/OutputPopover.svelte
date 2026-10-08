<script lang="ts">
  import { keepInView } from './keepInView'
  import { t } from './i18n.svelte'
  import { outside } from './outside'
  import { app, nameOf, sourceName, muteOut, setOutLevel } from './store.svelte'
  import { OUTPUTS, type OutputKey } from './model'
  import Fader from './Fader.svelte'

  // Edit the wire into an output: its level, or disconnect it (outputs always have a source on the
  // device, so "disconnected" is stored as muted).
  interface Props { x: number; y: number; output: OutputKey; color: string; onclose: () => void }
  let { x, y, output, color, onclose }: Props = $props()

  const o = $derived(app.profile!.mixer[output])
  const def = $derived(OUTPUTS.find(d => d.key === output)!)

</script>

<svelte:window onkeydown={e => e.key === 'Escape' && onclose()} />

<div class="pop" use:outside={onclose} use:keepInView style:left="{x}px" style:top="{y}px" style:--c={color} role="dialog" aria-label="Output">
  <div class="title">
    <span class="from">{sourceName(o.source)}</span>
    <span class="arrow">→</span>
    <span>{nameOf(`out:${output}`, def.name)}</span>
  </div>
  <Fader value={o.levelDbL} min={-89} max={0} {color} label={t('Output level')}
    onchange={v => setOutLevel(output, v, o.link)} />
  <button class="remove" onclick={() => { muteOut(output, true); onclose() }}>
    {t('Disconnect')}
  </button>
</div>

<style>
  .title { display: flex; gap: 6px; font-size: 12px; font-weight: 600; }
  .from { color: var(--c); }
  .arrow { color: var(--text-4); }
  .remove {
    align-self: flex-end;
    height: 22px; padding: 0 8px; border-radius: 4px; font-size: 11px;
    border: 1px solid var(--line-strong); color: var(--text-3);
  }
  .remove:hover { color: var(--warn); border-color: var(--warn); }
</style>

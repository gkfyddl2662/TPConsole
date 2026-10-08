<script lang="ts">
  import { keepInView } from './keepInView'
  import { outside } from './outside'
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { app, set, nameOf, mixName } from './store.svelte'
  import { MIXES } from './model'
  import type { SourceRow } from './sources'
  import Fader from './Fader.svelte'
  import Pan from './Pan.svelte'

  // Edit one send (source -> mix) in place, next to the wire that was clicked.
  interface Props { x: number; y: number; row: SourceRow; mix: number; onclose: () => void }
  let { x, y, row, mix, onclose }: Props = $props()

  const ch = $derived(app.profile!.mixer.mixes[mix].channel[row.channels[0]])
  const color = $derived(`var(--mix-${MIXES[mix].toLowerCase()})`)
  const setAll = (field: string, value: unknown) =>
    set(...row.channels.map(c => ({ path: `mixer.mixes.${mix}.channel.${c}.${field}`, value })))

</script>

<svelte:window onkeydown={e => e.key === 'Escape' && onclose()} />

<div class="pop" use:outside={onclose} use:keepInView style:left="{x}px" style:top="{y}px" style:--c={color} role="dialog" aria-label="Send">
  <div class="title">
    <span>{nameOf(row.key, row.fallback)}</span>
    <span class="arrow">→</span>
    <span class="to">{mixName(mix)}</span>
  </div>
  <Fader value={ch.levelDb} min={-89} max={12} {color} label={t('Send level')} onchange={v => setAll('levelDb', v)} />
  {#if row.channels.length === 1}
    <Pan value={ch.pan} onchange={v => setAll('pan', v)} />
  {/if}
  <div class="actions">
    <button class:on={ch.solo} class="solo" onclick={() => setAll('solo', !ch.solo)}>{t('Solo')}</button>
    <button class:on={ch.invert} onclick={() => setAll('invert', !ch.invert)} use:hint={() => t('Invert polarity')}>Ø</button>
    <span class="spacer"></span>
    <button class="remove" onclick={() => { setAll('mute', true); onclose() }}>{t('Disconnect')}</button>
  </div>
</div>

<style>
  .title { display: flex; gap: 6px; font-size: 12px; font-weight: 600; }
  .arrow { color: var(--text-4); }
  .to { color: var(--c); }
  .actions { display: flex; gap: 6px; align-items: center; }
  .actions button {
    height: 22px; padding: 0 8px; border-radius: 4px; font-size: 11px;
    border: 1px solid var(--line-strong); color: var(--text-3);
  }
  .actions button:hover { color: var(--text); }
  .actions button.on { color: var(--text); border-color: var(--text-3); }
  .actions .solo.on { color: var(--meter-mid); border-color: var(--meter-mid); }
  .spacer { flex: 1; }
  .remove:hover { color: var(--warn) !important; border-color: var(--warn) !important; }
</style>

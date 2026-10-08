<script lang="ts">
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { clamp } from './scale'

  // Linear pan 0 (left) .. 100 (right). Drawn as a thin bar growing from the centre.
  interface Props { value: number; onchange: (v: number) => void }
  let { value, onchange }: Props = $props()

  let el: HTMLDivElement
  let dragging = false
  const text = $derived(value === 50 ? 'C' : value < 50 ? `L${(50 - value) * 2}` : `R${(value - 50) * 2}`)

  function at(e: PointerEvent) {
    const r = el.getBoundingClientRect()
    let v = Math.round(clamp((e.clientX - r.left) / r.width, 0, 1) * 100)
    if (Math.abs(v - 50) <= 2) v = 50 // detent at centre
    if (v !== value) onchange(v)
  }
</script>

<div class="pan" use:hint={() => t('Pan · double-click: centre')}>
  <span class="side">L</span>
  <div
    class="track"
    bind:this={el}
    role="slider"
    tabindex="-1"
    aria-label="Pan"
    aria-valuemin={0}
    aria-valuemax={100}
    aria-valuenow={value}
    onpointerdown={e => { el.setPointerCapture(e.pointerId); dragging = true; at(e) }}
    onpointermove={e => dragging && at(e)}
    onpointerup={() => (dragging = false)}
    ondblclick={() => onchange(50)}
  >
    <div class="centre"></div>
    <div class="fill" style:left="{Math.min(value, 50)}%" style:width="{Math.abs(value - 50)}%"></div>
    <div class="dot" style:left="{value}%"></div>
  </div>
  <span class="side">R</span>
  <span class="val">{text}</span>
</div>

<style>
  .pan { display: flex; align-items: center; gap: 5px; height: 14px; }
  .side { font-size: 9px; color: var(--text-4); }
  .track { position: relative; flex: 1; height: 14px; cursor: ew-resize; touch-action: none; }
  .centre { position: absolute; left: 50%; top: 3px; bottom: 3px; width: 1px; background: var(--text-4); }
  .fill { position: absolute; top: 6px; height: 2px; background: var(--text-3); }
  .dot {
    position: absolute;
    top: 4px;
    width: 6px;
    height: 6px;
    margin-left: -3px;
    border-radius: 50%;
    background: var(--text-2);
  }
  .val { width: 30px; text-align: right; font-size: 10px; color: var(--text-3); }
</style>

<script lang="ts">
  import { outside } from './outside'
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { toPos, fromPos, clamp, fmtDb } from './scale'

  interface Props {
    /** dB, or with `volume` a Windows volume 0..1 (shown and edited as percent). */
    value: number | null | undefined
    min?: number
    max?: number
    color?: string
    dim?: boolean
    label: string
    /** Linear travel and no -inf (input gain). */
    linear?: boolean
    /** Windows app / device volume: 0..1 in, 0..1 out, shown as 0..100 %. */
    volume?: boolean
    compact?: boolean
    onchange: (v: number | null) => void
  }
  let { value, min = 0, max = 100, color = 'var(--text-3)', dim = false, label, linear = false, volume = false, compact = false, onchange }: Props = $props()

  // Volume faders work in percent internally.
  const v = $derived(volume ? Math.round((value ?? 1) * 100) : (value ?? null))
  const lin = $derived(linear || volume)
  const emit = (x: number | null) => onchange(volume ? (x ?? 0) / 100 : x)

  let track: HTMLDivElement
  let dragging = $state(false)
  let typing = $state(false)
  const focusSelect = (el: HTMLInputElement) => { el.focus(); el.select() }
  // Typed values: a number (dB or %), empty / "-inf" / "x" = off (outputs and sends only).
  let typed = $state<HTMLInputElement>()
  function commit() {
    if (!typing) return
    typing = false
    const raw = (typed?.value ?? '').trim().replace('−', '-')
    if (!lin && (raw === '' || /^-?inf|^x$/i.test(raw))) return update(null)
    const n = Math.round(Number(raw))
    if (Number.isFinite(n)) update(clamp(n, min, max))
  }
  const pos = $derived(lin ? ((v ?? min) - min) / (max - min) : toPos(v, max))
  // 0 dB is marked on dB faders that reach it; volume and gain have no unity mark.
  const ref = $derived(!lin && max > 0 ? 0 : null)
  const zero = $derived(ref === null ? null : toPos(ref, max))
  const text = $derived(volume ? `${v}` : linear && v ? '+' + v : fmtDb(v))
  const toValue = (p: number) => (lin ? Math.round(min + p * (max - min)) : fromPos(p, min, max))

  function at(e: PointerEvent) {
    const r = track.getBoundingClientRect()
    return clamp((e.clientX - r.left) / r.width, 0, 1)
  }
  function update(x: number | null) {
    if (x !== v) emit(x)
  }
  function down(e: PointerEvent) {
    if (e.button !== 0) return
    track.setPointerCapture(e.pointerId)
    dragging = true
    update(toValue(at(e)))
  }
  function move(e: PointerEvent) {
    if (dragging) update(toValue(at(e)))
  }
  function step(d: number) {
    const x = v === null ? (d > 0 ? min : null) : v + d
    update(x === null || x < min ? (lin ? min : null) : clamp(x, min, max))
  }
  function wheel(e: WheelEvent) {
    e.preventDefault()
    e.stopPropagation()
    step(e.deltaY < 0 ? 1 : -1)
  }
  function key(e: KeyboardEvent) {
    const d = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1 }[e.key]
    if (d) { e.preventDefault(); step(d) }
  }
</script>

<div class="fader" class:dim class:compact>
  <div
    class="track"
    class:dragging
    bind:this={track}
    role="slider"
    tabindex="0"
    aria-label={label}
    aria-valuemin={min}
    aria-valuemax={max}
    aria-valuenow={v ?? min - 1}
    aria-valuetext={v === null ? '-inf dB' : volume ? `${v}%` : `${v} dB`}
    use:hint={() => t(volume ? 'Double-click: reset · wheel: fine' : 'Double-click: 0 dB · wheel: 1 dB')}
    onpointerdown={down}
    onpointermove={move}
    onpointerup={() => (dragging = false)}
    onlostpointercapture={() => (dragging = false)}
    ondblclick={() => update(volume ? 100 : clamp(ref ?? 0, min, max))}
    onwheel={wheel}
    onkeydown={key}
  >
    <div class="rail"></div>
    {#if zero !== null}<div class="zero" style:left="{zero * 100}%" title={String(ref)}></div>{/if}
    <div class="fill" style:width="{pos * 100}%" style:background={color}></div>
    <div class="thumb" style:left="{pos * 100}%"></div>
  </div>
  {#if typing}
    <input class="db typed" value={v ?? ''} use:focusSelect use:outside={commit} bind:this={typed}
      onblur={commit} onkeydown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); if (e.key === 'Escape') typing = false }} />
  {:else}
    <button class="db" class:inf={v === null} use:hint={() => t('Click to type a value')} onclick={() => (typing = true)}>
      {text}
    </button>
  {/if}
</div>

<style>
  .fader { display: flex; align-items: center; gap: 8px; min-width: 0; }
  .track {
    position: relative;
    flex: 1;
    height: 22px;
    cursor: ew-resize;
    touch-action: none;
  }
  .rail, .fill {
    position: absolute;
    top: 50%;
    height: 3px;
    margin-top: -1.5px;
    border-radius: 2px;
  }
  .rail { left: 0; right: 0; background: var(--line-strong); }
  .fill { left: 0; opacity: 0.9; }
  /* Reference (unity) mark: a notch above and below the rail. */
  .zero {
    position: absolute;
    top: 4px;
    bottom: 4px;
    width: 2px;
    margin-left: -1px;
    border-radius: 1px;
    background: var(--text-3);
  }
  .compact .track { height: 16px; }
  .compact .thumb { top: 2px; bottom: 2px; }
  .compact .zero { top: 2px; bottom: 2px; }
  .compact .db { font-size: 10.5px; width: 24px; }
  .thumb {
    position: absolute;
    top: 4px;
    bottom: 4px;
    width: 4px;
    margin-left: -2px;
    border-radius: 2px;
    background: var(--text);
    box-shadow: 0 0 0 2px var(--bg);
    transition: transform 80ms;
  }
  .track:hover .thumb, .track.dragging .thumb { transform: scaleY(1.15); }
  .dim .fill { background: var(--text-4) !important; }
  .dim .thumb { background: var(--text-3); }
  .db {
    width: 26px;
    text-align: right;
    font-size: 11.5px;
    color: var(--text-2);
  }
  .db.inf { color: var(--text-4); }
  button.db { border-radius: 3px; }
  button.db:hover { color: var(--text); background: var(--hover); }
  .typed { width: 34px; height: 20px; padding: 0 3px; border: 1px solid var(--text-3); border-radius: 3px; background: var(--bg); color: var(--text); outline: none; }
  .dim .db { color: var(--text-4); }
</style>

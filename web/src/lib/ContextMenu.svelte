<script lang="ts" module>
  export type Item =
    | { label: string; sub?: string; checked?: boolean; disabled?: boolean; action: () => void }
    | { section: string }
    | { separator: true }
</script>

<script lang="ts">
  import { keepInView } from './keepInView'
  import { outside } from './outside'
  // Right-click menu. Lists everything a node or wire can do, so nothing is drag-only.
  interface Props { x: number; y: number; title?: string; items: Item[]; onclose: () => void }
  let { x, y, title, items, onclose }: Props = $props()

</script>

<svelte:window onkeydown={e => e.key === 'Escape' && onclose()} />

<div class="menu" use:outside={onclose} use:keepInView style:left="{x}px" style:top="{y}px" role="menu">
  {#if title}<div class="head">{title}</div>{/if}
  {#each items as it, i (i)}
    {#if 'separator' in it}
      <div class="sep"></div>
    {:else if 'section' in it}
      <div class="section">{it.section}</div>
    {:else}
      <button role="menuitem" disabled={it.disabled} class:on={it.checked} onclick={() => { it.action(); onclose() }}>
        <span class="dot"></span>
        <span class="main">{it.label}</span>
        {#if it.sub}<span class="sub">{it.sub}</span>{/if}
      </button>
    {/if}
  {/each}
</div>

<style>
  .menu {
    position: fixed;
    z-index: 20;
    min-width: 230px;
    max-height: 70vh;
    overflow: auto;
  }
  .head { padding: 4px 8px 6px; font-size: 11px; color: var(--text-3); }
  .section { padding: 6px 8px 2px; font-size: 10.5px; letter-spacing: 0.05em; text-transform: uppercase; color: var(--text-4); }
  button {
    width: 100%;
    display: grid;
    grid-template-columns: 10px 1fr auto;
    align-items: center;
    gap: 8px;
    padding: 6px 8px;
    border-radius: 5px;
    text-align: left;
    font-size: 12.5px;
  }
  button:hover:not(:disabled) { background: var(--hover); }
  button:disabled { color: var(--text-4); cursor: default; }
  .dot { width: 6px; height: 6px; border-radius: 50%; }
  .on .dot { background: var(--text); }
  .sub { font-size: 11px; color: var(--text-3); }
</style>

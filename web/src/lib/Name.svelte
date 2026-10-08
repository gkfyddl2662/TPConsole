<script lang="ts">
  import { t } from './i18n.svelte'
  import { hint } from './hint.svelte'
  import { nameOf, rename } from './store.svelte'
  import { outside } from './outside'

  // A label the user can rename (double-click or the pencil). Stored in profile.names[key].
  // `windows`: this name is also the Windows device's name; renaming asks for admin permission.
  // `display`: shown name when it comes from elsewhere (e.g. the synced Windows device name).
  interface Props { key: string; fallback: string; display?: string; windows?: boolean; onrename?: (name: string) => void }
  let { key, fallback, display, windows = false, onrename }: Props = $props()
  const shown = $derived(display ?? nameOf(key, fallback))

  let editing = $state(false)
  let input = $state<HTMLInputElement>()
  // Clicking elsewhere saves (like Windows Explorer); Esc or ✕ cancels.
  let cancelling = false

  async function start() {
    cancelling = false
    editing = true
    await Promise.resolve()
    input?.select()
  }
  function commit() {
    if (!editing || cancelling) return
    const value = (input?.value ?? '').trim()
    editing = false
    if (value === shown) return
    rename(key, value)
    onrename?.(value || fallback)
  }
  function cancel() {
    cancelling = true
    editing = false
  }
  const tip = () => t(windows ? 'Rename (also renames the Windows device)' : 'Rename')
</script>

{#if editing}
  <span class="edit" use:outside={commit}>
    <input
      bind:this={input}
      value={shown}
      placeholder={fallback}
      onblur={commit}
      onkeydown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') cancel() }}
    />
    <!-- pointerdown runs before the input's blur, so the choice wins over "click elsewhere saves" -->
    <button class="eb ok" aria-label={t('Save')} onpointerdown={e => { e.preventDefault(); commit() }}>✓</button>
    <button class="eb no" aria-label={t('Cancel')} onpointerdown={e => { e.preventDefault(); cancel() }}>✕</button>
  </span>
{:else}
  <span class="name" role="button" tabindex="-1" use:hint={() => t('Double-click to rename')} ondblclick={start}>{shown}</span>
  <button class="pencil" aria-label={tip()} use:hint={tip} onclick={e => { e.stopPropagation(); start() }}>
    <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.5 2.5l3 3L5 14H2v-3z" /></svg>
  </button>
{/if}

<style>
  .name { cursor: default; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
  /* The pencil shows when hovering the row/node the name belongs to. */
  .pencil {
    flex: none; display: grid; place-items: center; width: 18px; height: 18px; margin-left: 2px;
    border-radius: 3px; color: var(--text-3); opacity: 0; transition: opacity 80ms;
  }
  .pencil:hover { color: var(--text); background: var(--hover); }
  /* Visible while hovering the node / card / row that owns the name. */
  :global(:is(.node, article, .src, .mix, .row):hover) .pencil, .pencil:focus-visible { opacity: 1; }
  .edit { display: flex; align-items: center; gap: 2px; min-width: 0; flex: 1; }
  .eb { flex: none; width: 18px; height: 18px; border-radius: 3px; font-size: 11px; color: var(--text-3); }
  .eb.ok:hover { color: var(--meter-lo); background: var(--hover); }
  .eb.no:hover { color: var(--warn); background: var(--hover); }
  input {
    width: 100%;
    min-width: 0;
    background: var(--bg);
    border: 1px solid var(--line-strong);
    border-radius: 3px;
    padding: 0 4px;
    outline: none;
  }
</style>

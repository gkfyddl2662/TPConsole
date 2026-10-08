<script lang="ts">
  import { dialogState, answer } from './confirm.svelte'
  import { t } from './i18n.svelte'

  // Native modal: the browser handles the backdrop, focus trapping and Escape (cancel → false).
  const d = $derived(dialogState.current)
  let el = $state<HTMLDialogElement>()
  $effect(() => {
    if (d && el && !el.open) el.showModal()
    else if (!d && el?.open) el.close()
  })
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
<dialog bind:this={el} aria-label={d?.message} oncancel={e => { e.preventDefault(); answer(false) }}
  onclick={e => e.target === el && answer(false)}>
  {#if d}
    <p>{d.message}</p>
    <div class="actions">
      <button class="btn ghost" onclick={() => answer(false)}>{t('Cancel')}</button>
      <!-- svelte-ignore a11y_autofocus -->
      <button class="btn" class:danger={d.danger} autofocus onclick={() => answer(true)}>{d.ok}</button>
    </div>
  {/if}
</dialog>

<style>
  dialog {
    width: 380px; max-width: calc(100vw - 32px); padding: 18px 20px 16px; border-radius: 10px; color: var(--text);
    background: var(--raised); border: 1px solid var(--line-strong); box-shadow: 0 20px 50px var(--shadow);
  }
  dialog::backdrop { background: var(--backdrop); }
  p { margin: 0 0 16px; font-size: 13px; line-height: 1.5; white-space: pre-line; }
  .actions { display: flex; justify-content: flex-end; gap: 8px; }
  .btn { height: 28px; padding: 0 14px; border-radius: 5px; font-size: 12px; background: var(--text); color: var(--bg); }
  .btn.ghost { background: none; color: var(--text-2); border: 1px solid var(--line-strong); }
  .btn.danger { background: var(--warn); color: var(--on-warn); }
</style>

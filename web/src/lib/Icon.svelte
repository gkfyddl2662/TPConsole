<script lang="ts">
  // Small stroked icons drawn for this app (16px grid).
  interface Props { name: 'headphones' | 'link' | 'unlink'; size?: number }
  let { name, size = 14 }: Props = $props()
</script>

<svg width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true">
  {#if name === 'headphones'}
    <path d="M3 10V8a5 5 0 0 1 10 0v2" />
    <rect x="2.5" y="9.5" width="3" height="4" rx="1" />
    <rect x="10.5" y="9.5" width="3" height="4" rx="1" />
  {:else if name === 'link'}
    <path d="M6.5 9.5l3-3" />
    <path d="M7 4.5l1-1a2.8 2.8 0 0 1 4 4l-1 1" />
    <path d="M9 11.5l-1 1a2.8 2.8 0 0 1-4-4l1-1" />
  {:else if name === 'unlink'}
    <path d="M7 4.5l1-1a2.8 2.8 0 0 1 4 4l-1 1" />
    <path d="M9 11.5l-1 1a2.8 2.8 0 0 1-4-4l1-1" />
  {/if}
</svg>

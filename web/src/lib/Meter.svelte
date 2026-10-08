<script lang="ts">
  import { meterPos } from './scale'

  // Device meters arrive as dB x 10. One bar per value (mono or stereo).
  let { values }: { values: (number | undefined)[] } = $props()
</script>

<div class="meter">
  {#each values as v, i (i)}
    <!-- Three solid zones at fixed dB marks (green below -12 dB, amber to -3 dB, red above), uncovered
         from the low end. Solid blocks with a gap stay distinct at any zoom, unlike gradient stops. -->
    <div class="bar">
      <div class="lit" style="--p: {meterPos(v)}">
        <div class="zone lo"></div><div class="zone mid"></div><div class="zone hi"></div>
      </div>
    </div>
  {/each}
</div>

<style>
  .meter { display: flex; flex-direction: column; gap: 2px; }
  .bar { position: relative; height: 4px; border-radius: 1px; overflow: hidden; background: var(--raised); }
  .zone { position: absolute; top: 0; bottom: 0; }
  .lo { left: 0; width: 80%; background: var(--meter-lo); }
  .mid { left: calc(80% + 1px); width: calc(15% - 1px); background: var(--meter-mid); }
  .hi { left: calc(95% + 1px); right: 0; background: var(--meter-hi); }
  /* The lit part is the zones clipped at the level: nothing is drawn over them, so no sliver of
     colour can shimmer at a covering edge. */
  .lit { position: absolute; inset: 0; clip-path: inset(0 calc((1 - var(--p)) * 100%) 0 0); transition: clip-path var(--meter-ms, 67ms) linear; }
</style>

import { t } from './i18n.svelte'

/** Human text for a virtual-routing result from the host (errors only; null = fine). */
export function vrMessage(m: string | null | undefined): string | null {
  if (!m) return null
  if (m === 'REMOVE_PENDING') return t('The plugin is still being removed. Unplug and replug the E2x2 (or restart Windows) first.')
  if (!m.startsWith('RESTART_BLOCKED')) return m
  const [, apps, pending] = m.split('|')
  // Saved in the registry either way; Windows applies it when the E2x2 starts again.
  if (pending) return t('Windows refused to restart the E2x2 until it is re-plugged (an earlier restart was blocked). The settings are saved — unplug and replug the E2x2, or restart Windows.')
  return t('The E2x2 could not restart: {apps} had it open. The settings are saved — unplug and replug the E2x2 (or restart Windows) to apply them.', { apps: apps || t('an app') })
}

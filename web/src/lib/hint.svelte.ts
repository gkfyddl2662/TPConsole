// Hover hints: elements declare what can be done with them; the bar at the bottom shows it.
export const hintState = $state({ text: null as string | null })

/** use:hint={() => t('...')} — the function runs on hover so the text follows the current language. */
export function hint(el: Element, text: () => string) {
  let get = text
  const enter = () => (hintState.text = get())
  const leave = () => { if (hintState.text === get()) hintState.text = null }
  el.addEventListener('pointerenter', enter)
  el.addEventListener('pointerleave', leave)
  return {
    update: (t: () => string) => (get = t),
    destroy: () => { el.removeEventListener('pointerenter', enter); el.removeEventListener('pointerleave', leave) },
  }
}

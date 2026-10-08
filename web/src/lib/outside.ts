/**
 * Calls `done` when the user presses anywhere outside `node` (the background, another panel…).
 * Editing fields use it to finish like Windows Explorer does, without relying on blur, which does not
 * fire when the press lands on something that keeps the focus (e.g. the routing background's pan).
 */
export function outside(node: HTMLElement, done: () => void) {
  const onDown = (e: PointerEvent) => { if (!node.contains(e.target as Node)) done() }
  // Capture phase: runs before handlers that might stop the event.
  window.addEventListener('pointerdown', onDown, true)
  return { destroy: () => window.removeEventListener('pointerdown', onDown, true) }
}

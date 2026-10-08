/** Shifts an absolutely positioned popup so it stays inside the window (flips up/left when needed). */
export function keepInView(el: HTMLElement) {
  // After the content has rendered; getBoundingClientRect forces layout.
  setTimeout(() => {
    const r = el.getBoundingClientRect()
    const margin = 8
    let dx = 0
    let dy = 0
    if (r.bottom > innerHeight - margin) dy = Math.max(innerHeight - margin - r.bottom, margin - r.top)
    if (r.right > innerWidth - margin) dx = innerWidth - margin - r.right
    if (r.left + dx < margin) dx = margin - r.left
    if (dx || dy) el.style.translate = `${dx}px ${dy}px`
  })
}

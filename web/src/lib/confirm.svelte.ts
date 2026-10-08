// In-app confirmation dialog (replaces the browser's confirm()). Rendered once by Dialog.svelte.
type Pending = { message: string; ok: string; danger: boolean; resolve: (v: boolean) => void }

export const dialogState = $state({ current: null as Pending | null })

export function ask(message: string, ok: string, danger = false): Promise<boolean> {
  return new Promise(resolve => {
    dialogState.current?.resolve(false)
    dialogState.current = { message, ok, danger, resolve }
  })
}

export function answer(v: boolean) {
  dialogState.current?.resolve(v)
  dialogState.current = null
}

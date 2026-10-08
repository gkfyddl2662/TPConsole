import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// Built into the WPF host's wwwroot (served from https://app.tpconsole/).
export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: { outDir: '../src/TPConsole.App/wwwroot', emptyOutDir: true },
  server: { port: 5199, strictPort: true },
})

import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

/**
 * Separate from `vite.config.ts` so the Vite config stays a Vite config. Merging them makes the
 * dev server's type depend on the test runner's, which is the kind of coupling that turns a
 * version bump into an afternoon.
 *
 * Not in `tsconfig.json`'s include: Vitest ships its own copy of Vite, and type-checking this
 * file makes the compiler compare two structurally-identical `Plugin` types from two node_modules
 * paths. The file is checked by Vitest when it loads it, which is the only place it runs.
 */
export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
})

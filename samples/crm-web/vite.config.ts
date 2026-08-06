import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The sample's ASP.NET host. Proxied rather than pointed at directly so the browser
// treats the API as same-origin: the CORS policy in Program.cs is what a deployed
// client needs, and a dev server that depended on it would hide a broken one.
const API = process.env.CRM_API ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: { port: 5173, proxy: { '/api': { target: API, changeOrigin: true } } },
})

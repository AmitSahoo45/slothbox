import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
export default defineConfig(({ mode }) => ({
  plugins: [react(), tailwindcss(), {
    name: 'phone-page',
    generateBundle() {
      this.emitFile({
        type: 'asset', fileName: 'phone/index.html',
        source: readFileSync(new URL('./phone/index.html', import.meta.url), 'utf8'),
      })
    },
    configureServer(server) {
      if (mode !== 'mock') return
      server.middlewares.use((request, response, next) => {
        if (request.url?.split('?')[0] !== '/mockServiceWorker.js') return next()
        response.setHeader('Content-Type', 'application/javascript')
        response.setHeader('Cache-Control', 'no-store')
        response.end(readFileSync(require.resolve('msw/mockServiceWorker.js')))
      })
    },
  }],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true },
      '/events': { target: 'http://127.0.0.1:5080', changeOrigin: true },
      '/openapi': { target: 'http://127.0.0.1:5080', changeOrigin: true },
    },
  },
  build: { outDir: '../SlothBox.Server/wwwroot', emptyOutDir: true },
  test: { environment: 'jsdom', setupFiles: ['./tests/setup.ts'], maxWorkers: 1, testTimeout: 15000 },
}))

import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
  test: { environment: 'jsdom', setupFiles: ['./tests/setup.ts'] },
})

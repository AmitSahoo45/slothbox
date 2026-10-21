import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource/atkinson-hyperlegible-next/400.css'
import '@fontsource/atkinson-hyperlegible-next/700.css'
import '@fontsource/atkinson-hyperlegible-mono/400.css'
import '@fontsource/atkinson-hyperlegible-mono/700.css'
import './styles/tokens.css'
import './styles/base.css'
import { App } from './App'

async function start() {
  if (import.meta.env.DEV && import.meta.env.MODE === 'mock') {
    const { startMocks } = await import('./mocks/browser')
    await startMocks()
  }
  createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
}
void start().catch(() => {
  const root = document.getElementById('root')
  if (root) root.textContent = 'SlothBox could not start. Reload the page to try again.'
})

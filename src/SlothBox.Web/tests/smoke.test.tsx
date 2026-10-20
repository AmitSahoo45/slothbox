import { render, screen } from '@testing-library/react'
import { afterAll, beforeAll, expect, test, vi } from 'vitest'
import { App } from '../src/App'
import { server } from '../src/mocks/server'

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' })
  vi.stubGlobal('EventSource', class extends EventTarget { close() {} })
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
})
afterAll(() => { server.close(); vi.unstubAllGlobals() })
test('renders an accessible application entry point', async () => {
  window.history.replaceState({}, '', '/')
  render(<App />)
  expect(screen.getByRole('main')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Today', level: 1 })).toBeVisible()
  expect(await screen.findByText('Gemma ready')).toBeVisible()
})

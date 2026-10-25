import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses, status } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { createBrowserHandlers } from '../src/mocks/browser-handlers'
import type { Schema } from '../src/api/types'
import { setupUi, TestEvents } from './ui-helpers'

setupUi('/settings')
beforeEach(() => { localStorage.clear(); document.documentElement.classList.remove('dark') })
afterEach(() => { localStorage.clear(); document.documentElement.classList.remove('dark') })

test('System follows live system changes; explicit Light and Dark persist across navigation and reload', async () => {
  const media = new EventTarget()
  let dark = true
  vi.stubGlobal('matchMedia', (query: string) => query === '(prefers-color-scheme: dark)' ? Object.assign(media, { matches: dark })
    : { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })
  const changeSystem = (value: boolean) => act(() => {
    dark = value
    Object.assign(media, { matches: dark })
    media.dispatchEvent(new Event('change'))
  })
  const view = render(<App />)
  expect(await screen.findByRole('radio', { name: 'System' })).toBeChecked()
  expect(document.documentElement).toHaveClass('dark')
  changeSystem(false)
  expect(document.documentElement).not.toHaveClass('dark')
  fireEvent.click(screen.getByRole('radio', { name: 'Dark' }))
  expect(document.documentElement).toHaveClass('dark')
  changeSystem(false)
  expect(document.documentElement).toHaveClass('dark')
  fireEvent.click(screen.getByRole('radio', { name: 'Light' }))
  changeSystem(true)
  expect(document.documentElement).not.toHaveClass('dark')
  fireEvent.click(screen.getByRole('link', { name: 'Categories' }))
  expect(document.documentElement).not.toHaveClass('dark')
  view.unmount()
  window.history.replaceState({}, '', '/settings')
  render(<App />)
  expect(await screen.findByRole('radio', { name: 'Light' })).toBeChecked()
  expect(document.documentElement).not.toHaveClass('dark')
  fireEvent.click(screen.getByRole('radio', { name: 'System' }))
  expect(document.documentElement).toHaveClass('dark')
})

test('theme remains usable when localStorage is blocked', async () => {
  const get = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Blocked') })
  const set = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Blocked') })
  try {
    render(<App />)
    fireEvent.click(await screen.findByRole('radio', { name: 'Dark' }))
    expect(document.documentElement).toHaveClass('dark')
  } finally { get.mockRestore(); set.mockRestore() }
})

test('saves all server settings and retains them across navigation', async () => {
  server.use(...createBrowserHandlers())
  render(<App />)
  fireEvent.click(await screen.findByRole('switch', { name: 'Start SlothBox when I sign in' }))
  fireEvent.change(screen.getByLabelText('Seconds per receipt'), { target: { value: '45' } })
  fireEvent.change(screen.getByLabelText('Ollama address'), { target: { value: 'http://127.0.0.1:11437' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
  expect(await screen.findByText('Settings saved')).toBeVisible()
  fireEvent.click(screen.getByRole('link', { name: 'Categories' }))
  fireEvent.click(screen.getByRole('link', { name: 'Settings' }))
  expect(await screen.findByRole('switch', { name: 'Start SlothBox when I sign in' })).toBeChecked()
  expect(screen.getByLabelText('Seconds per receipt')).toHaveValue(45)
  expect(screen.getByLabelText('Seconds per screenshot')).toHaveValue(15)
  expect(screen.getByLabelText('Ollama address')).toHaveValue('http://127.0.0.1:11437')
})

test('a settings refetch preserves edits and includes newer untouched settings in the full PUT', async () => {
  let current: Schema['SettingsDto'] = structuredClone(responses.GetSettings)
  let body: unknown
  server.use(
    http.get('*/api/settings', () => HttpResponse.json(current)),
    http.put('*/api/settings', async ({ request }) => { const data = await request.json() as Schema['SettingsDto']; body = data; return HttpResponse.json(data) }),
  )
  render(<App />)
  fireEvent.change(await screen.findByLabelText('Seconds per receipt'), { target: { value: '60' } })
  current = { ...current, startAtLogin: true, ollamaUrl: 'http://127.0.0.1:11437' }
  act(() => TestEvents.latest.dispatchEvent(new Event('open')))
  await waitFor(() => expect(screen.getByRole('switch', { name: 'Start SlothBox when I sign in' })).toBeChecked())
  expect(screen.getByLabelText('Seconds per receipt')).toHaveValue(60)
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
  await waitFor(() => expect(body).toEqual({ startAtLogin: true, ollamaUrl: 'http://127.0.0.1:11437',
    laziness: { receiptSeconds: 60, screenshotSeconds: 15, documentSeconds: 15, otherSeconds: 5 } }))
})

test('failed settings saves show the API title, retain the draft and reject invalid durations', async () => {
  const title = 'Start at login is set by the SlothBox app on Windows, which is not running.'
  server.use(http.put('*/api/settings', () => HttpResponse.json({ title, status: 409 }, { status: 409 })))
  render(<App />)
  const receipt = await screen.findByLabelText('Seconds per receipt')
  fireEvent.change(receipt, { target: { value: '-1' } })
  expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled()
  fireEvent.change(receipt, { target: { value: '45' } })
  fireEvent.click(screen.getByRole('switch', { name: 'Start SlothBox when I sign in' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
  expect(await screen.findByText(title)).toBeVisible()
  expect(receipt).toHaveValue(45)
  expect(screen.getByRole('switch', { name: 'Start SlothBox when I sign in' })).toBeChecked()
})

test.each(['http://127.0.0.1:11436/?a=1', 'http://127.0.0.1:11436/#top', 'http://user@127.0.0.1:11436', 'ftp://127.0.0.1:11436'])(
  'an Ollama address the API refuses cannot be saved: %s', async address => {
    render(<App />)
    fireEvent.change(await screen.findByLabelText('Ollama address'), { target: { value: address } })
    expect(screen.getByText('Enter a complete http or https address, without a query, fragment or user name.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Save settings' })).toBeDisabled()
  })

test('an Ollama address behind a proxy is saved and shows the address the API returns', async () => {
  server.use(http.put('*/api/settings', async ({ request }) => {
    const body = await request.json() as Schema['UpdateSettingsRequest']
    expect(body.ollamaUrl).toBe('http://127.0.0.1:8080/ollama')
    return HttpResponse.json({ ...body, ollamaUrl: 'http://127.0.0.1:8080/ollama/' })
  }))
  render(<App />)
  fireEvent.change(await screen.findByLabelText('Ollama address'), { target: { value: ' http://127.0.0.1:8080/ollama ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
  await waitFor(() => expect(screen.getByLabelText('Ollama address')).toHaveValue('http://127.0.0.1:8080/ollama/'))
})

test('checks model status and reports search rebuild as queued work', async () => {
  let ready = false
  server.use(
    http.get('*/api/status', () => HttpResponse.json({ ...status, embeddingModel: { ...status.embeddingModel, loaded: ready } })),
    http.post('*/api/search-data/rebuild', () => HttpResponse.json({ queued: 7 })),
  )
  render(<App />)
  const ai = await screen.findByRole('region', { name: 'AI' })
  expect(within(ai).getByText(/embeddinggemma/)).toBeVisible()
  expect(within(ai).getByText('Installed, not loaded')).toBeVisible()
  ready = true
  fireEvent.click(within(ai).getByRole('button', { name: 'Check again' }))
  expect(await within(ai).findByText('Loaded on CPU')).toBeVisible()
  fireEvent.click(within(ai).getByRole('button', { name: 'Rebuild missing search data' }))
  expect(await screen.findByText('Queued 7 files for search data rebuild')).toBeVisible()
})

test('Allow again preserves a refused row, then removes it on success; undo events refresh the list', async () => {
  let allowed = false
  let success = false
  server.use(
    http.get('*/api/ignored', () => HttpResponse.json(allowed ? [] : responses.GetIgnored)),
    http.delete('*/api/ignored/:sha256', ({ params }) => {
      expect(params.sha256).toBe('a'.repeat(64))
      allowed = success
      return HttpResponse.json({ success, message: success ? null : 'Cannot allow this file yet.' })
    }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Allow again sample.jpg' }))
  expect(await screen.findByText('Cannot allow this file yet.')).toBeVisible()
  expect(screen.getByText('sample.jpg')).toBeVisible()
  success = true
  fireEvent.click(screen.getByRole('button', { name: 'Allow again sample.jpg' }))
  await waitFor(() => expect(screen.queryByText('sample.jpg')).not.toBeInTheDocument())
  allowed = false
  act(() => TestEvents.latest.emit('file.undone', { fileId: 'synthetic-file' }))
  expect(await screen.findByText('sample.jpg')).toBeVisible()
})

test('Allow again on a file that is no longer ignored shows the 404 title and refreshes the list', async () => {
  let listed = true
  server.use(
    http.get('*/api/ignored', () => HttpResponse.json(listed ? responses.GetIgnored : [])),
    http.delete('*/api/ignored/:sha256', () => {
      listed = false
      return HttpResponse.json({ title: 'This file is not on the ignored list.', status: 404 }, { status: 404 })
    }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Allow again sample.jpg' }))
  expect(await screen.findByText('This file is not on the ignored list.')).toBeVisible()
  await waitFor(() => expect(screen.queryByText('sample.jpg')).not.toBeInTheDocument())
  expect(screen.getByText('No ignored files.')).toBeVisible()
})

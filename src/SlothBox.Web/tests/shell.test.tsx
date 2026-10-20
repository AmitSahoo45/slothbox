import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeAll, beforeEach, afterEach, afterAll, expect, test, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { server } from '../src/mocks/server'
import { status } from '../src/mocks/fixtures'
import { readFileSync } from 'node:fs'
const shellCss = readFileSync('src/styles/base.css', 'utf8')

class TestEventSource extends EventTarget {
  static instances: TestEventSource[] = []
  close = vi.fn()
  constructor(public url: string) { super(); TestEventSource.instances.push(this) }
  emit(name: string, data: unknown) {
    this.dispatchEvent(new MessageEvent(name, { data: JSON.stringify(data) }))
  }
}
let narrow: boolean
let media: EventTarget
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
beforeEach(() => {
  narrow = false
  media = new EventTarget()
  window.history.replaceState({}, '', '/')
  TestEventSource.instances = []
  vi.stubGlobal('EventSource', TestEventSource)
  vi.stubGlobal('matchMedia', () => ({
    get matches() { return narrow },
    addEventListener: media.addEventListener.bind(media),
    removeEventListener: media.removeEventListener.bind(media),
  }))
})
afterEach(() => { server.resetHandlers(); vi.unstubAllGlobals() })
afterAll(() => server.close())

test('shows the navigation, review count, service state and queue', async () => {
  render(<App />)
  expect(await screen.findByText('2 files waiting')).toBeVisible()
  const nav = screen.getByRole('navigation', { name: 'Main' })
  expect(within(nav).getAllByRole('link')).toHaveLength(7)
  expect(await within(nav).findByRole('link', { name: /Needs review.*1/ })).toHaveAttribute('href', '/review')
  expect(screen.getByText('Gemma ready')).toBeVisible()
  expect(screen.getByText('Database ready')).toBeVisible()
})
test('a status.changed event immediately shows the Gemma offline banner', async () => {
  render(<App />)
  await screen.findByText('Gemma ready')
  act(() => TestEventSource.instances.at(-1)!.emit('status.changed', {
    ...status, gemma: { ready: false, message: null }, queue: { waiting: 4, processing: 0 },
  }))
  expect(await screen.findByText('Gemma is offline. 4 files are waiting. Start Docker Desktop and SlothBox will carry on by itself.')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Check again' })).toBeEnabled()
})
test('queue events update the waiting count without losing service status', async () => {
  render(<App />)
  await screen.findByText('Gemma ready')
  act(() => TestEventSource.instances.at(-1)!.emit('queue.changed', { waiting: '9', processing: 1 }))
  expect(await screen.findByText('9 files waiting')).toBeVisible()
  expect(screen.getByText('Gemma ready')).toBeVisible()
})
test.each([false, true])('a queue event survives an older in-flight status response (cached: %s)', async cached => {
  let release!: () => void
  const delayed = new Promise<void>(resolve => { release = resolve })
  let started!: () => void
  const requested = new Promise<void>(resolve => { started = resolve })
  const handler = http.get('*/api/status', async () => {
    started()
    await delayed
    return HttpResponse.json(status)
  })
  if (!cached) server.use(handler)
  render(<App />)
  if (cached) {
    await screen.findByText('Gemma ready')
    server.use(handler)
    act(() => TestEventSource.instances.at(-1)!.dispatchEvent(new Event('open')))
  }
  await requested
  act(() => TestEventSource.instances.at(-1)!.emit('queue.changed', { waiting: 12, processing: 0 }))
  release()
  expect(await screen.findByText('12 files waiting')).toBeVisible()
  expect(await screen.findByText('Gemma ready')).toBeVisible()
})
test('sidebar collapses from 232px to 64px below 1024px and keeps links named', async () => {
  const style = document.createElement('style')
  style.textContent = shellCss
  document.head.append(style)
  try {
    render(<App />)
    const sidebar = await screen.findByRole('complementary', { name: 'Sidebar' })
    expect(getComputedStyle(sidebar).width).toBe('232px')
    act(() => { narrow = true; media.dispatchEvent(new Event('change')) })
    expect(getComputedStyle(sidebar).width).toBe('64px')
    expect(screen.getByRole('link', { name: 'Settings' })).toBeInTheDocument()
  } finally { style.remove() }
})
test('Pause sends the protection header, updates status and offers Resume', async () => {
  let header: string | null = null
  server.use(http.post('*/api/pause', ({ request }) => {
    header = request.headers.get('X-SlothBox')
    return HttpResponse.json({ ...status, paused: true })
  }))
  render(<App />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Pause' }))
  expect(await screen.findByText('SlothBox is paused. 2 files are waiting.')).toBeVisible()
  expect(header).toBe('1')
  expect(screen.getAllByRole('button', { name: 'Resume' })).toHaveLength(2)
  fireEvent.click(screen.getAllByRole('button', { name: 'Resume' })[0])
  await screen.findByRole('button', { name: 'Pause' })
})
test('failed mutations show the ProblemDetails message without changing status', async () => {
  server.use(http.post('*/api/pause', () => HttpResponse.json(
    { title: 'Cannot pause', detail: 'The database is unavailable.' }, { status: 503 })))
  render(<App />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Pause' }))
  expect(await screen.findByText('The database is unavailable.')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled()
})
test('unavailable status is honest, retryable and does not say services are ready', async () => {
  server.use(http.get('*/api/status', () => HttpResponse.json(
    { title: 'Not implemented', status: 501 }, { status: 501 })))
  render(<App />)
  expect(await screen.findByText(/Could not check SlothBox/)).toBeVisible()
  expect(screen.queryByText('Gemma ready')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Check again' })).toBeEnabled()
})
test('setup hides the sidebar, and incomplete setup redirects there', async () => {
  server.use(http.get('*/api/status', () => HttpResponse.json({ ...status, setupComplete: false })))
  render(<App />)
  expect(await screen.findByRole('heading', { name: 'Set up SlothBox' })).toBeVisible()
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument()
  expect(window.location.pathname).toBe('/setup')
})
test('disconnect is visible, reconnect refreshes status, and unmount closes SSE', async () => {
  const app = render(<App />)
  await screen.findByText('Gemma ready')
  const source = TestEventSource.instances.at(-1)!
  act(() => source.dispatchEvent(new Event('error')))
  expect(await screen.findByText(/Live updates disconnected/)).toBeVisible()
  server.use(http.get('*/api/status', () => HttpResponse.json({ ...status, paused: true })))
  act(() => source.dispatchEvent(new Event('open')))
  await screen.findByText('SlothBox is paused. 2 files are waiting.')
  await waitFor(() => expect(screen.queryByText(/Live updates disconnected/)).not.toBeInTheDocument())
  app.unmount()
  expect(source.close).toHaveBeenCalledOnce()
})
test('global shortcuts open help with Escape focus return, and slash navigates without stealing typing', async () => {
  render(<App />)
  const settings = await screen.findByRole('link', { name: 'Settings' })
  act(() => settings.focus())
  fireEvent.keyDown(document, { key: '?' })
  expect(await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })).toBeVisible()
  fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(settings).toHaveFocus()
  fireEvent.keyDown(document, { key: '/' })
  expect(await screen.findByRole('heading', { name: 'Search' })).toBeVisible()
  const input = document.createElement('input')
  document.body.append(input)
  input.focus()
  fireEvent.keyDown(input, { key: '?' })
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  input.remove()
})

import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses, activity } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { setupUi, TestEvents } from './ui-helpers'

setupUi()

test.each([
  [0, 'under a minute'], [1, '1 minute'], [59, '59 minutes'], [72, '1 hour 12 minutes'],
])('meter formats %s estimated minutes and defaults to this week', async (minutesSaved, text) => {
  const ranges: string[] = []
  server.use(http.get('*/api/stats/laziness', ({ request }) => {
    ranges.push(new URL(request.url).searchParams.get('range')!)
    return HttpResponse.json({ ...responses.GetLazinessStats, minutesSaved })
  }))
  render(<App />)
  expect(await screen.findByText(`That's about ${text} of doing nothing.`)).toBeVisible()
  expect(screen.getByRole('radio', { name: 'This week' })).toBeChecked()
  expect(ranges).toContain('week')
})

test('range selection requests its data and zero has the specified copy', async () => {
  server.use(http.get('*/api/stats/laziness', ({ request }) => HttpResponse.json({
    ...responses.GetLazinessStats,
    filesFiled: new URL(request.url).searchParams.get('range') === 'week' ? 0 : 412,
    minutesSaved: 123,
  })))
  render(<App />)
  expect(await screen.findByText("SlothBox hasn't filed anything this week. It's waiting, patiently.")).toBeVisible()
  fireEvent.click(screen.getByRole('radio', { name: 'All time' }))
  expect(await screen.findByText('Since you set it up, SlothBox filed 412 files for you.')).toBeVisible()
  expect(screen.getByText("That's about 2 hours 3 minutes of doing nothing.")).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'How we count' }))
  expect(await screen.findByText('Receipt: 30 seconds')).toBeVisible()
})

test('bars and their table use the current Monday to Sunday, with zero stubs and no future bars', async () => {
  const today = new Date()
  const monday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (today.getDay() + 6) % 7)
  const date = `${monday.getFullYear()}-${String(monday.getMonth() + 1).padStart(2, '0')}-${String(monday.getDate()).padStart(2, '0')}`
  server.use(http.get('*/api/stats/laziness', () => HttpResponse.json({
    ...responses.GetLazinessStats, days: [{ date, count: '14' }],
  })))
  render(<App />)
  const table = await screen.findByRole('table', { name: 'Files filed this week' })
  const rows = within(table).getAllByRole('row')
  expect(rows).toHaveLength(8)
  expect(within(rows[1]).getByText('14')).toBeInTheDocument()
  const bars = screen.getByLabelText('Files filed this week chart')
  expect(bars.querySelector('[data-day="0"]')).toHaveAttribute('data-count', '14')
  const todayIndex = (today.getDay() + 6) % 7
  expect(bars.querySelector(`[data-day="${todayIndex}"]`)).toHaveAttribute('data-today', 'true')
  for (let day = todayIndex + 1; day < 7; day++) {
    expect(bars.querySelector(`[data-day="${day}"]`)).toHaveAttribute('data-future', 'true')
    expect(within(rows[day + 1]).getByText('Not yet')).toBeInTheDocument()
  }
})

test('activity paginates with the opaque cursor and limit 50; undo stays a separate action', async () => {
  const cursors: (string | null)[] = []
  const undo = { count: 0, header: '' }
  server.use(
    http.get('*/api/activity', ({ request }) => {
      const url = new URL(request.url)
      expect(url.searchParams.get('limit')).toBe('50')
      cursors.push(url.searchParams.get('cursor'))
      return HttpResponse.json(url.searchParams.has('cursor')
        ? { items: [{ ...activity, fileId: 'older', newName: 'older.pdf', canUndo: false }], nextCursor: null }
        : { items: [activity], nextCursor: 'opaque+/==' })
    }),
    http.post('*/api/files/:id/undo', ({ request }) => {
      undo.count++
      undo.header = request.headers.get('X-SlothBox')!
      return HttpResponse.json(responses.UndoFile)
    }),
  )
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  fireEvent.click(within(list).getByRole('button', { name: 'Undo' }))
  expect(await screen.findByText('Undone')).toBeVisible()
  expect(undo).toEqual({ count: 1, header: '1' })
  expect(window.location.search).toBe('')
  fireEvent.click(screen.getByRole('button', { name: 'Show more' }))
  expect(await screen.findByText('older.pdf')).toBeVisible()
  expect(cursors).toContain('opaque+/==')
  expect(within(list).getAllByRole('button', { name: 'Undo' }).at(-1)).toBeDisabled()
})

test('new files wait behind a pill while scrolled and duplicate SSE deliveries count once', async () => {
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  await within(list).findByText(activity.newName)
  Object.defineProperty(window, 'scrollY', { configurable: true, value: 600 })
  fireEvent.scroll(window)
  const incoming = [1, 2, 3].map(i => ({ ...activity, fileId: `new-${i}`, newName: `new-${i}.jpg`, at: new Date().toISOString() }))
  act(() => {
    incoming.forEach(item => TestEvents.latest.emit('file.filed', item))
    TestEvents.latest.emit('file.filed', incoming[0])
  })
  expect(await screen.findByRole('button', { name: '3 new files' })).toBeVisible()
  expect(within(list).queryByText('new-1.jpg')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: '3 new files' }))
  expect(await within(list).findByText('new-1.jpg')).toBeVisible()
  expect(within(list).getAllByRole('listitem')).toHaveLength(4)
})

test('undo events refresh authoritative state after undoing a refile instead of disabling further undo', async () => {
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  await within(list).findByText(activity.newName)
  server.use(http.get('*/api/activity', () => HttpResponse.json({
    items: [{ ...activity, category: 'Restored category', canUndo: true }], nextCursor: null,
  })))
  act(() => TestEvents.latest.emit('file.undone', { fileId: activity.fileId }))
  expect(await within(list).findByText('Receipts / Restored category')).toBeVisible()
  expect(within(list).getByRole('button', { name: 'Undo' })).toBeEnabled()
  expect(within(list).getByText('Filed')).toBeVisible()
})

test('an arrival before the initial activity response is retained', async () => {
  let release!: () => void
  const delayed = new Promise<void>(resolve => { release = resolve })
  server.use(http.get('*/api/activity', async () => {
    await delayed
    return HttpResponse.json({ items: [activity], nextCursor: null })
  }))
  render(<App />)
  await screen.findByText('Loading activity…')
  act(() => TestEvents.latest.emit('file.filed', { ...activity, fileId: 'new', newName: 'fresh.jpg', at: new Date().toISOString() }))
  expect(await screen.findByText('fresh.jpg')).toBeInTheDocument()
  release()
  await screen.findByText(activity.newName)
  expect(screen.getByText('fresh.jpg')).toBeInTheDocument()
})

test('reconnect while at the top puts newer results first', async () => {
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  await within(list).findByText(activity.newName)
  server.use(http.get('*/api/activity', () => HttpResponse.json({
    items: [{ ...activity, fileId: 'new', newName: 'reconnected.jpg', at: new Date().toISOString() }, activity], nextCursor: null,
  })))
  act(() => TestEvents.latest.dispatchEvent(new Event('open')))
  await within(list).findByText('reconnected.jpg')
  expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent('reconnected.jpg')
})

test('a late refetch cannot revert newer SSE filing details', async () => {
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  await within(list).findByText(activity.newName)
  let release!: () => void
  let started!: () => void
  const delayed = new Promise<void>(resolve => { release = resolve })
  const requested = new Promise<void>(resolve => { started = resolve })
  server.use(http.get('*/api/activity', async () => {
    started()
    await delayed
    return HttpResponse.json({ items: [activity], nextCursor: null })
  }))
  act(() => TestEvents.latest.dispatchEvent(new Event('open')))
  await requested
  act(() => TestEvents.latest.emit('file.refiled', { ...activity, category: 'New category' }))
  await within(list).findByText('Receipts / New category')
  release()
  await waitFor(() => expect(within(list).queryByText('Receipts / Food and drink')).not.toBeInTheDocument())
  // Let the delayed response settle before asserting the authoritative event remains.
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 50)) })
  expect(within(list).getByText('Receipts / New category')).toBeVisible()
})

test('a refile event received before initial load supplies newer details without a new-files pill', async () => {
  let release!: () => void
  const delayed = new Promise<void>(resolve => { release = resolve })
  server.use(http.get('*/api/activity', async () => {
    await delayed
    return HttpResponse.json({ items: [activity], nextCursor: null })
  }))
  render(<App />)
  await screen.findByText('Loading activity…')
  act(() => TestEvents.latest.emit('file.refiled', { ...activity, category: 'Latest category' }))
  release()
  expect(await screen.findByText('Receipts / Latest category')).toBeVisible()
  expect(screen.queryByRole('button', { name: /new files?/ })).not.toBeInTheDocument()
})

test('an undo during initial activity load restarts the request for authoritative state', async () => {
  let release!: () => void
  let started!: () => void
  let calls = 0
  const delayed = new Promise<void>(resolve => { release = resolve })
  const requested = new Promise<void>(resolve => { started = resolve })
  server.use(http.get('*/api/activity', async () => {
    if (++calls === 1) {
      started(); await delayed
      return HttpResponse.json({ items: [activity], nextCursor: null })
    }
    return HttpResponse.json({ items: [{ ...activity, status: 'review', category: 'Restored review' }], nextCursor: null })
  }))
  render(<App />)
  await requested
  act(() => TestEvents.latest.emit('file.undone', { fileId: activity.fileId }))
  release()
  expect(await screen.findByText('Receipts / Restored review')).toBeVisible()
})

test('Undo since confirms before posting, then reports actual results and partial failures', async () => {
  let sent: unknown
  server.use(http.post('*/api/undo/since', async ({ request }) => {
    sent = await request.json()
    return HttpResponse.json({ undone: 12, results: [
      responses.UndoFile, { ...responses.UndoFile, success: false, reason: 'Original folder is unavailable.' },
    ] })
  }))
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Undo since…' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Last hour' }))
  const dialog = await screen.findByRole('dialog', { name: /Undo actions since/ })
  expect(sent).toBeUndefined()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Undo actions' }))
  expect(await screen.findByText('Undone 12 actions')).toBeVisible()
  expect(await screen.findByText(/Original folder is unavailable/)).toBeVisible()
  expect(sent).toEqual({ since: expect.any(String) })
})

test('failed undo shows its reason and never claims success', async () => {
  server.use(http.post('*/api/files/:id/undo', () => HttpResponse.json({
    ...responses.UndoFile, success: false, reason: 'The original path is occupied.',
  })))
  render(<App />)
  const list = await screen.findByRole('list', { name: 'Activity' })
  fireEvent.click(await within(list).findByRole('button', { name: 'Undo' }))
  expect(await screen.findByText('The original path is occupied.')).toBeVisible()
  expect(screen.queryByText('Undone')).not.toBeInTheDocument()
})

test('empty feed offers the phone QR and API errors are retryable', async () => {
  server.use(http.get('*/api/activity', () => HttpResponse.json({ items: [], nextCursor: null })))
  render(<App />)
  expect(await screen.findByText('Nothing filed yet. Save a file to a watched folder, take a screenshot, or send one from your phone.')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Show phone QR code' }))
  const dialog = await screen.findByRole('dialog', { name: 'Send files from your phone' })
  expect(await within(dialog).findByRole('img', { name: 'Phone upload QR code' })).toBeVisible()
  fireEvent.keyDown(dialog, { key: 'Escape' })
  server.use(http.get('*/api/activity', () => HttpResponse.json({ title: 'Activity unavailable' }, { status: 503 })))
  act(() => TestEvents.latest.dispatchEvent(new Event('open')))
  expect(await screen.findByText('Activity unavailable')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Retry activity' })).toBeEnabled()
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
})

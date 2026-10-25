import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import type { Schema } from '../src/api/types'
import { setupUi, TestEvents } from './ui-helpers'

setupUi('/review')
const categories = ['Food', 'Shopping', 'Bills and recharges'].map((name, i) => ({
  ...responses.GetCategories[0], id: i + 1, name,
}))
function readyReceipt() {
  const item = structuredClone(responses.GetReview[0]) as Schema['ReviewItemDto']
  item.file.receipt = { ...responses.GetFile.receipt }
  item.reason = 'bad_output'
  return item
}

test('3 then Enter refiles with the third category; toast Undo calls the undo endpoint', async () => {
  const refile: Schema['RefileRequest'][] = []
  let undo = 0
  server.use(
    http.get('*/api/categories', () => HttpResponse.json(categories)),
    http.get('*/api/review', () => HttpResponse.json(refile.length ? [] : [readyReceipt()])),
    http.post('*/api/files/:id/refile', async ({ request }) => {
      expect(request.headers.get('X-SlothBox')).toBe('1')
      refile.push(await request.json() as Schema['RefileRequest'])
      return HttpResponse.json({ ...responses.GetFile, categoryId: 3, file: {
        ...responses.GetFile.file, category: 'Bills and recharges',
      } })
    }),
    http.post('*/api/files/:id/undo', () => { undo++; return HttpResponse.json(responses.UndoFile) }),
  )
  render(<App />)
  await screen.findByRole('button', { name: '3 Bills and recharges' })
  fireEvent.keyDown(document, { key: '3' })
  fireEvent.keyDown(document, { key: 'Enter' })
  expect(await screen.findByText('Filed to Receipts / Bills and recharges')).toBeVisible()
  expect(refile[0]).toMatchObject({ type: 'receipt', categoryId: 3, receipt: { amount: 120, paymentMethod: 'cash' } })
  fireEvent.click(screen.getByRole('button', { name: 'Undo' }))
  await waitFor(() => expect(undo).toBe(1))
})

test('a refused File it shows the API message and keeps the item for another try', async () => {
  server.use(
    http.get('*/api/categories', () => HttpResponse.json(categories)),
    http.get('*/api/review', () => HttpResponse.json([readyReceipt()])),
    http.post('*/api/files/:id/refile', () =>
      HttpResponse.json({ title: 'Nothing to change.', status: 400 }, { status: 400 })),
  )
  render(<App />)
  const save = await screen.findByRole('button', { name: 'File it' })
  await waitFor(() => expect(save).toBeEnabled())
  fireEvent.click(save)
  expect(await screen.findByText('Nothing to change.')).toBeVisible()
  expect(screen.getByText('sample.jpg')).toBeVisible()
  expect(screen.queryByText(/^Filed to/)).not.toBeInTheDocument()
  expect(save).toBeEnabled()
})

test('missing amount disables File it until supplied, and typing shortcuts does not hijack fields', async () => {
  render(<App />)
  const save = await screen.findByRole('button', { name: 'File it' })
  expect(await screen.findByText('No amount found on this receipt.')).toBeVisible()
  expect(save).toBeDisabled()
  const amount = screen.getByLabelText('Amount')
  act(() => amount.focus())
  fireEvent.keyDown(amount, { key: 's' })
  fireEvent.change(amount, { target: { value: '599.00' } })
  expect(save).toBeEnabled()
  expect(screen.getByLabelText('Vendor')).toHaveValue('Sample Cafe')
  fireEvent.change(amount, { target: { value: '-1' } })
  expect(save).toBeEnabled()
})

test('a new review arrival does not replace the item being edited', async () => {
  render(<App />)
  const title = await screen.findByLabelText('Title')
  fireEvent.change(title, { target: { value: 'My corrected title' } })
  const incoming = readyReceipt()
  incoming.file.file.fileId = 'incoming'
  incoming.file.file.title = 'New arrival'
  server.use(http.get('*/api/review', () => HttpResponse.json([incoming, responses.GetReview[0]])))
  act(() => TestEvents.latest.emit('file.review', { ...incoming.file.file, reason: incoming.reason }))
  await screen.findByText('1 of 2 left')
  expect(screen.getByLabelText('Title')).toHaveValue('My corrected title')
})

test('a review event during the first GET refreshes instead of retaining its stale empty response', async () => {
  let release!: () => void
  let started!: () => void
  let calls = 0
  const delayed = new Promise<void>(resolve => { release = resolve })
  const requested = new Promise<void>(resolve => { started = resolve })
  server.use(http.get('*/api/review', async () => {
    if (++calls === 1) { started(); await delayed; return HttpResponse.json([]) }
    return HttpResponse.json(responses.GetReview)
  }))
  render(<App />)
  await requested
  act(() => TestEvents.latest.emit('file.review', { ...responses.GetReview[0].file.file, reason: 'missing_amount' }))
  release()
  expect(await screen.findByRole('button', { name: 'File it' })).toBeVisible()
})

test('Skip posts the current id and moves to the next without changing its status', async () => {
  let skipped = false
  const first = readyReceipt()
  const next = readyReceipt()
  next.file.file.fileId = 'second'
  next.file.originalName = 'second.jpg'
  server.use(
    http.get('*/api/review', () => HttpResponse.json(skipped ? [next, first] : [first, next])),
    http.post('*/api/review/:id/skip', ({ params, request }) => {
      expect(params.id).toBe(first.file.file.fileId)
      expect(request.headers.get('X-SlothBox')).toBe('1')
      skipped = true
      return HttpResponse.json({ success: true, message: null })
    }),
  )
  render(<App />)
  await screen.findByText('sample.jpg')
  fireEvent.keyDown(document, { key: 's' })
  expect(await screen.findByText('second.jpg')).toBeVisible()
  expect(screen.getByText('1 of 2 left')).toBeVisible()
  expect(skipped).toBe(true)
})

test('failed skip keeps the current item and shows the API message', async () => {
  server.use(http.post('*/api/review/:id/skip', () => HttpResponse.json({ success: false, message: 'Could not skip.' })))
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Skip for now' }))
  expect(await screen.findByText('Could not skip.')).toBeVisible()
  expect(screen.getByText('sample.jpg')).toBeVisible()
})

test('non-receipt types hide receipt fields and changing type clears an incompatible category', async () => {
  server.use(http.get('*/api/categories', () => HttpResponse.json([
    ...categories, { ...categories[0], id: 4, type: 'document', name: 'Learning' },
  ])))
  render(<App />)
  fireEvent.change(await screen.findByLabelText('Type'), { target: { value: 'document' } })
  expect(screen.queryByLabelText('Amount')).not.toBeInTheDocument()
  expect(screen.getByLabelText('Title')).toBeVisible()
  expect(screen.getByRole('button', { name: 'File it' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: '1 Learning' }))
  expect(screen.getByRole('button', { name: 'File it' })).toBeEnabled()
})

test('empty review is calm, and later categories are available in More', async () => {
  server.use(http.get('*/api/categories', () => HttpResponse.json(
    Array.from({ length: 10 }, (_, i) => ({ ...categories[0], id: i + 1, name: `Category ${i + 1}` })),
  )))
  const { unmount } = render(<App />)
  const more = await screen.findByRole('button', { name: 'More categories' })
  fireEvent.keyDown(more, { key: 'Enter' })
  const item = await screen.findByRole('menuitemradio', { name: 'Category 10' })
  fireEvent.click(item)
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  expect(screen.getByRole('button', { name: 'More categories' })).toHaveTextContent('Category 10')
  unmount()
  server.use(http.get('*/api/review', () => HttpResponse.json([])))
  render(<App />)
  expect(await screen.findByText('Nothing needs you. SlothBox handled everything.')).toBeVisible()
  expect(within(screen.getByRole('main')).queryByRole('button', { name: 'File it' })).not.toBeInTheDocument()
})

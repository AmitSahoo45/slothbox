import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses, fileId, activity } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import type { Schema } from '../src/api/types'
import { setupUi } from './ui-helpers'

setupUi()

test('?file= loads details over any route; Escape preserves other query parameters', async () => {
  window.history.replaceState({}, '', `/search?q=lunch&file=${fileId}`)
  render(<App />)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  expect(await within(drawer).findByText('Sample lunch')).toBeVisible()
  expect(within(drawer).getByText('₹120.00')).toBeVisible()
  expect(within(drawer).getByRole('heading', { name: 'History' })).toBeVisible()
  fireEvent.keyDown(drawer, { key: 'Escape' })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(window.location.search).toBe('?q=lunch')
  expect(screen.getByRole('heading', { name: 'Search' })).toHaveFocus()
})

test('row opens drawer, open/reveal use API ids, and Escape returns focus to that row', async () => {
  const calls: string[] = []
  server.use(...['open', 'reveal'].map(action => http.post(`*/api/files/:id/${action}`, ({ params, request }) => {
    expect(params.id).toBe(fileId)
    expect(request.headers.get('X-SlothBox')).toBe('1')
    calls.push(action)
    return HttpResponse.json({ success: true, message: null })
  })))
  render(<App />)
  const row = await screen.findByRole('button', { name: `View ${activity.newName}` })
  act(() => row.focus())
  fireEvent.click(row)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  fireEvent.click(await within(drawer).findByRole('button', { name: 'Open' }))
  await waitFor(() => expect(calls).toContain('open'))
  fireEvent.click(within(drawer).getByRole('button', { name: 'Show in folder' }))
  await waitFor(() => expect(calls).toEqual(['open', 'reveal']))
  fireEvent.keyDown(drawer, { key: 'Escape' })
  await waitFor(() => expect(row).toHaveFocus())
})

test('Change refiles the selected type/category and exposes Undo', async () => {
  let body: unknown
  let undos = 0
  server.use(
    http.get('*/api/categories', () => HttpResponse.json([
      ...responses.GetCategories, { ...responses.GetCategories[0], id: 2, name: 'Shopping' },
    ])),
    http.post('*/api/files/:id/refile', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json({ ...responses.GetFile, categoryId: 2, file: { ...activity, category: 'Shopping' } })
    }),
    http.post('*/api/files/:id/undo', () => { undos++; return HttpResponse.json(responses.UndoFile) }),
  )
  window.history.replaceState({}, '', `/?file=${fileId}`)
  render(<App />)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  fireEvent.click(await within(drawer).findByRole('button', { name: 'Change' }))
  fireEvent.change(await within(drawer).findByLabelText('Category'), { target: { value: '2' } })
  fireEvent.click(within(drawer).getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByText('Filed to Receipts / Shopping')).toBeInTheDocument()
  expect(body).toMatchObject({ type: 'receipt', categoryId: 2, receipt: { paymentMethod: 'cash' } })
  expect(within(drawer).getByText('Receipt / Shopping')).toBeVisible()
  const notifications = within(drawer).getByRole('region', { name: 'Notifications (F8)' })
  fireEvent.click(within(notifications).getByRole('button', { name: 'Undo' }))
  await waitFor(() => expect(undos).toBe(1))
  await within(notifications).findByText('Undone')
  fireEvent.keyDown(drawer, { key: 'Escape' })
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'File details' })).not.toBeInTheDocument())
})

test('Change sends an unreported payment method as unknown, so the API never reads it as UPI', async () => {
  let body: Schema['RefileRequest'] | undefined
  const receipt: Partial<Schema['ReceiptDto']> = { ...responses.GetFile.receipt }
  delete receipt.paymentMethod
  server.use(
    http.get('*/api/files/:id', () => HttpResponse.json({ ...responses.GetFile, receipt })),
    http.post('*/api/files/:id/refile', async ({ request }) => {
      body = await request.json() as Schema['RefileRequest']
      return HttpResponse.json(responses.Refile)
    }),
  )
  window.history.replaceState({}, '', `/?file=${fileId}`)
  render(<App />)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  fireEvent.click(await within(drawer).findByRole('button', { name: 'Change' }))
  const save = within(drawer).getByRole('button', { name: 'Save changes' })
  await waitFor(() => expect(save).toBeEnabled())
  fireEvent.click(save)
  await waitFor(() => expect(body?.receipt).toEqual({ ...receipt, paymentMethod: 'unknown' }))
})

test('a refused Change shows the API message and keeps the form, with no Undo offered', async () => {
  server.use(http.post('*/api/files/:id/refile', () =>
    HttpResponse.json({ title: 'Nothing to change.', status: 400 }, { status: 400 })))
  window.history.replaceState({}, '', `/?file=${fileId}`)
  render(<App />)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  fireEvent.click(await within(drawer).findByRole('button', { name: 'Change' }))
  const save = within(drawer).getByRole('button', { name: 'Save changes' })
  await waitFor(() => expect(save).toBeEnabled())
  fireEvent.click(save)
  expect(await within(drawer).findByText('Nothing to change.')).toBeVisible()
  expect(save).toBeEnabled()
  expect(screen.queryByText(/^Filed to/)).not.toBeInTheDocument()
  const notifications = within(drawer).getByRole('region', { name: 'Notifications (F8)' })
  expect(within(notifications).queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument()
})

test('missing file gives retryable error; no fake details or mutation controls', async () => {
  server.use(http.get('*/api/files/:id', () => HttpResponse.json({ title: 'File not found' }, { status: 404 })))
  window.history.replaceState({}, '', '/?file=missing')
  render(<App />)
  const drawer = await screen.findByRole('dialog', { name: 'File details' })
  expect(await within(drawer).findByText('File not found')).toBeVisible()
  expect(within(drawer).getByRole('button', { name: 'Retry file' })).toBeEnabled()
  expect(within(drawer).queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument()
})

test('history displays the timestamp date and time in the same local timezone', async () => {
  const at = '2026-10-20T23:45:00Z'
  const dateFormatter = Date.prototype.toLocaleDateString
  // A specific timezone makes the midnight boundary deterministic on every test machine.
  Date.prototype.toLocaleDateString = function (locale?: Intl.LocalesArgument, options?: Intl.DateTimeFormatOptions) {
    return dateFormatter.call(this, locale, { ...options, timeZone: 'Asia/Kolkata' })
  }
  try {
    server.use(http.get('*/api/files/:id', () => HttpResponse.json({
      ...responses.GetFile, history: [{ ...responses.GetFile.history[0], at }],
    })))
    window.history.replaceState({}, '', `/?file=${fileId}`)
    render(<App />)
    const drawer = await screen.findByRole('dialog', { name: 'File details' })
    expect(await within(drawer).findByText(/21 Oct 2026,/)).toBeVisible()
  } finally { Date.prototype.toLocaleDateString = dateFormatter }
})

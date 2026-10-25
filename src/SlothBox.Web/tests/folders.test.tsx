import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { setupUi, TestEvents } from './ui-helpers'

setupUi('/folders')
test('refused folder states the rule and can pick another; cloud mode is locked to Copy only', async () => {
  let accepted = false
  let creates = 0
  server.use(
    http.post('*/api/folders/check', () => HttpResponse.json({ accepted, reason: accepted ? 'Cloud folder' : 'A drive root cannot be watched.', copyOnly: accepted, existingFileCount: 12 })),
    http.post('*/api/folders', async ({ request }) => {
      expect(await request.json()).toMatchObject({ mode: 'copy', tidyExisting: false })
      creates++
      return HttpResponse.json(responses.CreateFolder)
    }),
  )
  render(<App />)
  const add = await screen.findByRole('button', { name: 'Add folder' })
  await waitFor(() => expect(add).toBeEnabled())
  fireEvent.click(add)
  expect(await screen.findByText('A drive root cannot be watched.')).toBeVisible()
  accepted = true
  fireEvent.click(screen.getByRole('button', { name: 'Pick another folder' }))
  const mode = await screen.findByLabelText('Mode')
  expect(mode).toBeDisabled()
  expect(mode).toHaveValue('copy')
  expect(screen.getByText('12 files are already here')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Watch folder' }))
  await waitFor(() => expect(creates).toBe(1))
})
test('tidy confirms first, receives progress, and missing folders can be removed', async () => {
  let tidy = 0
  server.use(
    http.get('*/api/folders', () => HttpResponse.json([responses.GetFolders[0], { ...responses.GetFolders[0], id: 2, label: 'Missing input', missing: true }])),
    http.post('*/api/folders/:id/tidy-existing', () => { tidy++; return HttpResponse.json({ folderId: 1, queued: 3 }) }),
  )
  render(<App />)
  expect(await screen.findByText('Folder not found')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Tidy them now' }))
  expect(tidy).toBe(0)
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Tidy files' }))
  await waitFor(() => expect(tidy).toBe(1))
  act(() => TestEvents.latest.emit('folder.tidy-progress', { folderId: 1, done: 1, total: 3 }))
  expect(await screen.findByText('Tidying 1 of 3')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Remove Missing input' })).toBeEnabled()
})
test('headless Add folder uses a path field', async () => {
  server.use(http.get('*/api/setup', () => HttpResponse.json({ ...responses.GetSetup, nativePickerAvailable: false })))
  render(<App />)
  const add = await screen.findByRole('button', { name: 'Add folder' })
  await waitFor(() => expect(add).toBeEnabled())
  fireEvent.click(add)
  const input = await screen.findByLabelText('Folder path')
  fireEvent.change(input, { target: { value: 'D:\\Sample-Input' } })
  fireEvent.click(screen.getByRole('button', { name: 'Check folder' }))
  expect(await screen.findByText('3 files are already here')).toBeVisible()
})

test('closing a folder confirmation returns keyboard focus to its opener', async () => {
  render(<App />)
  const button = await screen.findByRole('button', { name: 'Tidy them now' })
  act(() => button.focus())
  fireEvent.click(button)
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(button).toHaveFocus())
})

const missingFolder = { ...responses.GetFolders[0], id: 2, label: 'Missing input', path: 'D:\\Old-Input', missing: true, enabled: false, baselineCount: 0 }

test('Locate sends only the new path; a refused path shows the API title and the folder is watched once accepted', async () => {
  let located = false
  const bodies: unknown[] = []
  server.use(
    http.get('*/api/setup', () => HttpResponse.json({ ...responses.GetSetup, nativePickerAvailable: false })),
    http.get('*/api/folders', () => HttpResponse.json([located ? { ...missingFolder, path: 'D:\\New-Input', missing: false } : missingFolder])),
    http.patch('*/api/folders/:id', async ({ request, params }) => {
      expect(params.id).toBe('2')
      const body = await request.json() as { path: string }
      bodies.push(body)
      if (body.path === 'D:\\') return HttpResponse.json({ title: 'Pick a folder inside the drive.', detail: 'Synthetic technical note.', status: 400 }, { status: 400 })
      located = true
      return HttpResponse.json({ ...missingFolder, path: body.path, missing: false })
    }),
  )
  render(<App />)
  expect(await screen.findByText('Folder not found')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Remove Missing input' })).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Locate Missing input' }))
  const dialog = await screen.findByRole('dialog', { name: 'Locate Missing input' })
  const input = within(dialog).getByLabelText('Folder path')
  fireEvent.change(input, { target: { value: 'D:\\' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Locate folder' }))
  expect(await within(dialog).findByText('Pick a folder inside the drive.')).toBeVisible()
  expect(screen.queryByText('Synthetic technical note.')).not.toBeInTheDocument()
  fireEvent.change(input, { target: { value: ' D:\\New-Input ' } })
  expect(within(dialog).queryByText('Pick a folder inside the drive.')).not.toBeInTheDocument()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Locate folder' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(bodies).toEqual([{ path: 'D:\\' }, { path: 'D:\\New-Input' }])
  expect(await screen.findByText('Watching Missing input')).toBeVisible()
  expect(await screen.findByText('D:\\New-Input')).toBeVisible()
  expect(screen.queryByText('Folder not found')).not.toBeInTheDocument()
})

test('Locate on a folder still at its path shows the 409 title and refreshes the folder list', async () => {
  let reads = 0
  server.use(
    http.get('*/api/folders', () => { reads++; return HttpResponse.json([missingFolder]) }),
    http.post('*/api/system/pick-folder', () => HttpResponse.json({ path: 'D:\\Somewhere-Else' })),
    http.patch('*/api/folders/:id', async ({ request }) => {
      expect(await request.json()).toEqual({ path: 'D:\\Somewhere-Else' })
      return HttpResponse.json({ title: 'The folder is still where it was. Locate only points a missing folder at its new place.', status: 409 }, { status: 409 })
    }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Locate Missing input' }))
  const before = reads
  expect(await screen.findByText('The folder is still where it was. Locate only points a missing folder at its new place.')).toBeVisible()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await waitFor(() => expect(reads).toBeGreaterThan(before))
})

test('Locate falls back to a typed path when the folder dialog is unavailable; cancelling the dialog closes Locate', async () => {
  let picks = 0
  server.use(
    http.get('*/api/folders', () => HttpResponse.json([missingFolder])),
    http.post('*/api/system/pick-folder', () => {
      picks++
      return picks === 1 ? HttpResponse.json({ path: null })
        : HttpResponse.json({ title: "The folder dialog isn't available. Type or paste the folder path instead.", status: 409 }, { status: 409 })
    }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Locate Missing input' }))
  await waitFor(() => expect(picks).toBe(1))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  fireEvent.click(screen.getByRole('button', { name: 'Locate Missing input' }))
  const dialog = await screen.findByRole('dialog', { name: 'Locate Missing input' })
  expect(await within(dialog).findByText("The folder dialog isn't available. Type or paste the folder path instead.")).toBeVisible()
  expect(within(dialog).getByLabelText('Folder path')).toBeEnabled()
})

test('a folder API refusal shows its ProblemDetails title', async () => {
  server.use(http.patch('*/api/folders/:id', () => HttpResponse.json(
    { title: 'Switch the folder on first.', detail: 'Synthetic technical note.', status: 409 }, { status: 409 })))
  render(<App />)
  fireEvent.click(await screen.findByRole('switch', { name: 'Watch Sample input' }))
  expect(await screen.findByText('Switch the folder on first.')).toBeVisible()
  expect(screen.queryByText('Synthetic technical note.')).not.toBeInTheDocument()
})

test('the last tidy-progress event reports the finished batch and refreshes the folder', async () => {
  let tidied = false
  server.use(http.get('*/api/folders', () => HttpResponse.json([tidied ? { ...responses.GetFolders[0], baselineCount: 0, filedCount: 4 } : responses.GetFolders[0]])))
  render(<App />)
  expect(await screen.findByRole('button', { name: 'Tidy them now' })).toBeVisible()
  act(() => TestEvents.latest.emit('folder.tidy-progress', { folderId: 1, done: 0, total: 3 }))
  expect(await screen.findByText('Tidying 0 of 3')).toBeVisible()
  tidied = true
  act(() => TestEvents.latest.emit('folder.tidy-progress', { folderId: 1, done: 3, total: 3 }))
  expect(await screen.findByText('Tidied 3 files')).toBeVisible()
  expect(await screen.findByText('4 filed')).toBeVisible()
  expect(screen.queryByRole('button', { name: 'Tidy them now' })).not.toBeInTheDocument()
})

test('the phone block shows the URL and QR code, and New link confirms before rotating', async () => {
  let rotations = 0
  server.use(http.post('*/api/phone/rotate', () => { rotations++; return HttpResponse.json(responses.RotatePhone) }))
  render(<App />)
  const block = await screen.findByRole('region', { name: 'Phone page' })
  expect(await within(block).findByText(responses.GetPhone.url)).toBeVisible()
  expect(within(block).getByRole('img', { name: 'Phone upload QR code' })).toHaveAttribute('src', expect.stringContaining('data:image/svg+xml'))
  fireEvent.click(within(block).getByRole('button', { name: 'New link' }))
  const dialog = await screen.findByRole('dialog', { name: 'Create a new phone link?' })
  expect(within(dialog).getByText('The old link will stop working.')).toBeVisible()
  expect(rotations).toBe(0)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Create new link' }))
  expect(await screen.findByText('New link ready')).toBeVisible()
  expect(rotations).toBe(1)
  expect(await within(block).findByText(responses.RotatePhone.url)).toBeVisible()
})

test.each([
  { enabled: true, copy: 'No Wi-Fi address found. Connect this laptop to Wi-Fi to use the phone page.' },
  { enabled: false, copy: 'The phone page is switched off.' },
])('the phone block without a link says why (enabled: $enabled)', async ({ enabled, copy }) => {
  server.use(http.get('*/api/phone', () => HttpResponse.json({ ...responses.GetPhone, enabled, url: null, qrSvg: null })))
  render(<App />)
  const block = await screen.findByRole('region', { name: 'Phone page' })
  expect(await within(block).findByText(copy)).toBeVisible()
  expect(within(block).queryByRole('button', { name: 'New link' })).not.toBeInTheDocument()
  expect(within(block).queryByRole('img')).not.toBeInTheDocument()
})

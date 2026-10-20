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

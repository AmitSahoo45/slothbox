import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { createBrowserHandlers } from '../src/mocks/browser-handlers'
import { setupUi, TestEvents } from './ui-helpers'

setupUi('/categories')

test('A22 locks a populated folder and prevents removal while allowing display edits', async () => {
  let body: unknown
  server.use(http.put('*/api/categories/:id', async ({ request }) => {
    body = await request.json()
    return HttpResponse.json({ ...responses.GetCategories[0], name: 'Meals' })
  }))
  render(<App />)
  const edit = await screen.findByRole('button', { name: 'Edit Food and drink' })
  act(() => edit.focus())
  fireEvent.click(edit)
  expect(screen.getByLabelText('Folder name')).toBeDisabled()
  expect(screen.getByText('The folder name is fixed once files are in it.')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Remove category' })).toBeDisabled()
  expect(screen.getByRole('tooltip', { hidden: true })).toHaveTextContent('Move its files to another category first.')
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Meals' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save category' }))
  await waitFor(() => expect(body).toMatchObject({ name: 'Meals', folder: null }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(edit).toHaveFocus()
})

test('A22 permits an empty category folder edit and removal, but preserves errors', async () => {
  let removed = false
  let accepted = false
  server.use(
    http.get('*/api/categories', () => HttpResponse.json(removed ? [] : [{ ...responses.GetCategories[0], fileCount: 0 }])),
    http.delete('*/api/categories/:id', () => {
      removed = accepted
      return HttpResponse.json({ success: accepted, message: accepted ? null : 'A file just arrived. Refresh and try again.' })
    }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Food and drink' }))
  expect(screen.getByLabelText('Folder name')).toBeEnabled()
  fireEvent.click(screen.getByRole('button', { name: 'Remove category' }))
  expect(await screen.findByText('A file just arrived. Refresh and try again.')).toBeVisible()
  expect(screen.getByRole('dialog')).toBeVisible()
  accepted = true
  fireEvent.click(screen.getByRole('button', { name: 'Remove category' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.queryByRole('button', { name: 'Edit Food and drink' })).not.toBeInTheDocument()
})

test('a file event locks the folder without losing the category draft', async () => {
  let count = 0
  let body: unknown
  server.use(
    http.get('*/api/categories', () => HttpResponse.json([{ ...responses.GetCategories[0], fileCount: count }])),
    http.put('*/api/categories/:id', async ({ request }) => { body = await request.json(); return HttpResponse.json(responses.GetCategories[0]) }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Food and drink' }))
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Meals' } })
  fireEvent.change(screen.getByLabelText('Folder name'), { target: { value: 'Draft-folder' } })
  count = 1
  act(() => TestEvents.latest.emit('file.filed', {}))
  await waitFor(() => expect(screen.getByLabelText('Folder name')).toBeDisabled())
  expect(screen.getByLabelText('Name')).toHaveValue('Meals')
  expect(screen.getByLabelText('Folder name')).toHaveValue(responses.GetCategories[0].folder)
  fireEvent.click(screen.getByRole('button', { name: 'Save category' }))
  await waitFor(() => expect(body).toMatchObject({ folder: null }))
})

test('saving a dirty description preserves a refreshed name and empty folder', async () => {
  let current = { ...responses.GetCategories[0], fileCount: 0 }
  let body: unknown
  server.use(
    http.get('*/api/categories', () => HttpResponse.json([current])),
    http.put('*/api/categories/:id', async ({ request }) => { body = await request.json(); return HttpResponse.json(current) }),
  )
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Food and drink' }))
  fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'My draft description' } })
  current = { ...current, name: 'Meals', folder: 'Meal-receipts' }
  act(() => TestEvents.latest.emit('file.filed', {}))
  await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Meals'))
  expect(screen.getByLabelText('Folder name')).toHaveValue('Meal-receipts')
  expect(screen.getByLabelText('Description')).toHaveValue('My draft description')
  fireEvent.click(screen.getByRole('button', { name: 'Save category' }))
  await waitFor(() => expect(body).toEqual({ name: 'Meals', folder: 'Meal-receipts', description: 'My draft description' }))
})

test('adds a category with a default folder, catches duplicate names, and shares edits across pages', async () => {
  server.use(...createBrowserHandlers())
  render(<App />)
  const add = await screen.findByRole('button', { name: 'Add category' })
  await waitFor(() => expect(add).toBeEnabled())
  fireEvent.click(add)
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Food and drink' } })
  expect(screen.getByRole('button', { name: 'Add category' })).toBeDisabled()
  expect(screen.getByText('A category with this name already exists in Receipts.')).toBeVisible()
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Pets' } })
  expect(screen.getByLabelText('Folder name')).toHaveValue('Pets')
  fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Pet food and vet bills' } })
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add category' }))
  expect(await screen.findByRole('button', { name: 'Edit Pets' })).toBeVisible()
  fireEvent.click(screen.getByRole('link', { name: 'Search' }))
  const select = await screen.findByLabelText('Category')
  expect(within(select).getByRole('option', { name: /Pets/ })).toBeInTheDocument()
})

test('built-in categories and the Other tab are read-only', async () => {
  server.use(http.get('*/api/categories', () => HttpResponse.json([
    { ...responses.GetCategories[0], name: 'Other', isSystem: true, fileCount: 0 },
    { ...responses.GetCategories[0], id: 2, type: 'other', name: 'Installers', isSystem: true, fileCount: 0 },
  ])))
  render(<App />)
  expect(await screen.findByText('Other (built in)')).toBeVisible()
  expect(screen.queryByRole('button', { name: /^Edit / })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('radio', { name: 'Other' }))
  expect(screen.getByText('Installers (built in)')).toBeVisible()
  expect(screen.queryByRole('button', { name: 'Add category' })).not.toBeInTheDocument()
})

test('a category API refusal shows its ProblemDetails title and keeps the draft', async () => {
  server.use(http.post('*/api/categories', () => HttpResponse.json(
    { title: 'A category with this name or folder already exists.', detail: 'Synthetic technical note.', status: 409 }, { status: 409 })))
  render(<App />)
  const add = await screen.findByRole('button', { name: 'Add category' })
  await waitFor(() => expect(add).toBeEnabled())
  fireEvent.click(add)
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Travel' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Add category' }))
  expect(await screen.findByText('A category with this name or folder already exists.')).toBeVisible()
  expect(screen.queryByText('Synthetic technical note.')).not.toBeInTheDocument()
  expect(within(dialog).getByLabelText('Name')).toHaveValue('Travel')
})

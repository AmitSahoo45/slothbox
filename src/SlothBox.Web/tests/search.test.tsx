import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { setupUi } from './ui-helpers'

setupUi('/search')
test('empty copy and 400 ms debounce; Enter searches immediately', async () => {
  const queries: string[] = []
  server.use(http.get('*/api/search', ({ request }) => {
    queries.push(new URL(request.url).searchParams.get('q')!)
    return HttpResponse.json({ items: [] })
  }))
  render(<App />)
  const input = await screen.findByRole('searchbox')
  expect(screen.getByText('Try: jio bill, reddit thread about dotnet, electricity October')).toBeVisible()
  const startedAt = performance.now()
  fireEvent.change(input, { target: { value: 'jio' } })
  expect(queries).toHaveLength(0)
  await waitFor(() => expect(queries).toEqual(['jio']))
  expect(performance.now() - startedAt).toBeGreaterThanOrEqual(390)
  expect(await screen.findByText('No files match. Try fewer words, or describe what the file was about.')).toBeVisible()
  fireEvent.change(input, { target: { value: 'electricity' } })
  fireEvent.submit(input.closest('form')!)
  await waitFor(() => expect(queries).toContain('electricity'))
})

test('all three match labels, filters, and drawer links reflect the response', async () => {
  const urls: URL[] = []
  server.use(http.get('*/api/search', ({ request }) => {
    urls.push(new URL(request.url))
    return HttpResponse.json({ items: [[true, false], [false, true], [true, true]].map(([matchedWords, matchedMeaning], i) => ({
      ...responses.Search.items[0], matchedWords, matchedMeaning,
      file: { ...responses.GetFile, file: { ...responses.GetFile.file, fileId: `file-${i}`, title: `Result ${i}` } },
    })) })
  }))
  render(<App />)
  const input = await screen.findByRole('searchbox')
  fireEvent.change(input, { target: { value: 'bill' } })
  fireEvent.submit(input.closest('form')!)
  for (const label of ['Matched words', 'Matched meaning', 'Matched words and meaning']) expect(await screen.findByText(label)).toBeVisible()
  expect(screen.getByRole('link', { name: 'Result 0' })).toHaveAttribute('href', expect.stringContaining('file=file-0'))
  fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'receipt' } })
  fireEvent.change(screen.getByLabelText('Category'), { target: { value: '1' } })
  fireEvent.change(screen.getByLabelText('From date'), { target: { value: '2026-10-01' } })
  await waitFor(() => expect(urls.at(-1)?.searchParams.get('from')).toBe('2026-10-01'))
  expect(urls.at(-1)?.searchParams.get('categoryId')).toBe('1')
})

test('slash focuses search, and clicking the result caption opens its drawer', async () => {
  window.history.replaceState({}, '', '/search?q=Sample')
  render(<App />)
  await screen.findByText('Synthetic lunch receipt')
  fireEvent.keyDown(document, { key: '/' })
  await waitFor(() => expect(screen.getByRole('searchbox')).toHaveFocus())
  fireEvent.click(screen.getByText('Synthetic lunch receipt'))
  expect(await screen.findByRole('dialog', { name: 'File details' })).toBeVisible()
})

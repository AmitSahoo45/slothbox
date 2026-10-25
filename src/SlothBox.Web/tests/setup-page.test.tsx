import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import type { Schema } from '../src/api/types'
import { setupUi } from './ui-helpers'

setupUi('/setup')
beforeEach(() => server.use(http.get('*/api/setup', () => HttpResponse.json({ ...responses.GetSetup, complete: false }))))
async function checks() {
  fireEvent.click(await screen.findByRole('button', { name: 'Continue' }))
  expect(await screen.findByText('Which folders should SlothBox watch?')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
  return screen.findByRole('button', { name: 'Finish setup' })
}
test('setup follows three steps, keeps drafts going back, and submits only on Finish', async () => {
  const sent: Schema['CompleteSetupRequest'][] = []
  server.use(http.post('*/api/setup/complete', async ({ request }) => {
    sent.push(await request.json() as Schema['CompleteSetupRequest'])
    return HttpResponse.json(responses.GetSetup)
  }))
  render(<App />)
  expect(await screen.findByText('Where should SlothBox put sorted files?')).toBeVisible()
  fireEvent.change(screen.getByLabelText('Output folder'), { target: { value: 'D:\\Filed-Test' } })
  const finish = await checks()
  expect(finish).toBeEnabled()
  expect(sent).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Back' }))
  fireEvent.click(screen.getByRole('button', { name: 'Back' }))
  expect(screen.getByLabelText('Output folder')).toHaveValue('D:\\Filed-Test')
  fireEvent.click(await checks())
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0]).toMatchObject({ outputRoot: 'D:\\Filed-Test', finishAnyway: false })
  expect(await screen.findByRole('heading', { name: 'Today', level: 1 })).toBeVisible()
})
test('checks repeat every 10 seconds and Finish anyway sends paused completion intent', async () => {
  let reads = 0
  let body: Schema['CompleteSetupRequest'] | undefined
  server.use(
    http.get('*/api/setup', () => { reads++; return HttpResponse.json({ ...responses.GetSetup, complete: false,
      status: { ...responses.GetStatus, embeddingModel: { ...responses.GetStatus.embeddingModel, present: false } } }) }),
    http.post('*/api/setup/complete', async ({ request }) => {
      body = await request.json() as Schema['CompleteSetupRequest']
      return HttpResponse.json({ ...responses.GetSetup, status: { ...responses.GetStatus, paused: true } })
    }),
  )
  render(<App />)
  expect(await checks()).toBeDisabled()
  await waitFor(() => expect(reads).toBeGreaterThanOrEqual(2))
  const baseline = reads
  const startedAt = performance.now()
  await waitFor(() => expect(reads).toBeGreaterThan(baseline), { timeout: 12000 })
  expect(performance.now() - startedAt).toBeGreaterThan(9000)
  fireEvent.click(screen.getByRole('button', { name: 'Finish anyway' }))
  await waitFor(() => expect(body?.finishAnyway).toBe(true))
}, 25000)

test('completed setup redirects to Today without exposing an editable output root', async () => {
  server.use(http.get('*/api/setup', () => HttpResponse.json(responses.GetSetup)))
  render(<App />)
  expect(await screen.findByRole('heading', { name: 'Today', level: 1 })).toBeVisible()
  expect(screen.queryByLabelText('Output folder')).not.toBeInTheDocument()
})

test('overlapping selected watched folders cannot advance to completion', async () => {
  server.use(http.get('*/api/setup', () => HttpResponse.json({ ...responses.GetSetup, complete: false,
    suggestedFolders: [{ path: 'D:\\Input', label: 'Input', mode: 'move' }, { path: 'D:\\Input\\Nested', label: 'Nested', mode: 'move' }],
  })))
  render(<App />)
  fireEvent.click(await screen.findByRole('button', { name: 'Continue' }))
  expect(await screen.findByText('Which folders should SlothBox watch?')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
  expect(await screen.findByText('Watched folders cannot contain each other.')).toBeVisible()
  expect(screen.queryByRole('button', { name: 'Finish setup' })).not.toBeInTheDocument()
})

test('step 1 asks the output-folder check before Continue and shows its reason when refused', async () => {
  const paths: string[] = []
  server.use(http.post('*/api/setup/check-output', async ({ request }) => {
    const { path } = await request.json() as Schema['CheckFolderRequest']
    paths.push(path)
    return HttpResponse.json(path === 'D:\\Watched\\Filed'
      ? { accepted: false, reason: 'SlothBox would refile its own files. Pick a folder outside the watched folders.' }
      : { accepted: true, reason: null })
  }))
  render(<App />)
  const output = await screen.findByLabelText('Output folder')
  fireEvent.change(output, { target: { value: ' D:\\Watched\\Filed ' } })
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
  expect(await screen.findByText('SlothBox would refile its own files. Pick a folder outside the watched folders.')).toBeVisible()
  expect(screen.getByText('Where should SlothBox put sorted files?')).toBeVisible()
  fireEvent.change(output, { target: { value: 'D:\\Filed-Test' } })
  expect(screen.queryByText('SlothBox would refile its own files. Pick a folder outside the watched folders.')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
  expect(await screen.findByText('Which folders should SlothBox watch?')).toBeVisible()
  expect(paths).toEqual(['D:\\Watched\\Filed', 'D:\\Filed-Test'])
})

test('a refused completion shows the API title and stays on the checks', async () => {
  server.use(http.post('*/api/setup/complete', () => HttpResponse.json(
    { title: 'Sample input: Pick a folder inside the drive.', status: 400 }, { status: 400 })))
  render(<App />)
  fireEvent.click(await checks())
  expect(await screen.findByText('Sample input: Pick a folder inside the drive.')).toBeVisible()
  expect(screen.getByRole('button', { name: 'Finish setup' })).toBeEnabled()
})

test('a completion refused because setup already finished leaves for Today', async () => {
  let finished = false
  server.use(
    http.get('*/api/setup', () => HttpResponse.json({ ...responses.GetSetup, complete: finished })),
    http.post('*/api/setup/complete', () => {
      finished = true
      return HttpResponse.json({ title: "Setup is already finished. The output folder can't be changed in this version.", status: 409 }, { status: 409 })
    }),
  )
  render(<App />)
  fireEvent.click(await checks())
  expect(await screen.findByRole('heading', { name: 'Today', level: 1 })).toBeVisible()
})

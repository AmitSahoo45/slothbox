// @vitest-environment node
import { afterAll, afterEach, beforeAll, expect, test } from 'vitest'
import { server } from '../src/mocks/server'
import { createBrowserHandlers } from '../src/mocks/browser-handlers'
import type { Schema } from '../src/api/types'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())
const base = 'http://127.0.0.1:5173'

test('browser mocks retain Pause and Resume across status reads and enforce the mutation header', async () => {
  server.use(...createBrowserHandlers())
  expect((await fetch(`${base}/api/pause`, { method: 'POST' })).status).toBe(403)
  await fetch(`${base}/api/pause`, { method: 'POST', headers: { 'X-SlothBox': '1' } })
  expect(await (await fetch(`${base}/api/status`)).json()).toMatchObject({ paused: true })
  await fetch(`${base}/api/resume`, { method: 'POST', headers: { 'X-SlothBox': '1' } })
  expect(await (await fetch(`${base}/api/status`)).json()).toMatchObject({ paused: false })
})
test('browser SSE stays open and sends a new status when Pause is clicked', async () => {
  server.use(...createBrowserHandlers())
  const controller = new AbortController()
  const response = await fetch(`${base}/events`, { signal: controller.signal })
  const reader = response.body!.getReader()
  try {
    const first = await reader.read()
    expect(first.done).toBe(false)
    expect(new TextDecoder().decode(first.value)).toContain('"paused":false')
    await fetch(`${base}/api/pause`, { method: 'POST', headers: { 'X-SlothBox': '1' } })
    const next = await reader.read()
    expect(next.done).toBe(false)
    expect(new TextDecoder().decode(next.value)).toContain('"paused":true')
  } finally {
    controller.abort()
    await reader.cancel().catch(() => undefined)
  }
})

test('browser review supports category selection, skip, refile and undo with persistent state', async () => {
  server.use(...createBrowserHandlers())
  const get = async (path: string) => (await fetch(`${base}/api/${path}`)).json()
  const post = async (path: string, body?: unknown) => (await fetch(`${base}/api/${path}`, {
    method: 'POST', headers: { 'X-SlothBox': '1', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })).json()
  const categories: Schema['CategoryDto'][] = await get('categories')
  expect(categories.filter(item => item.type === 'receipt').length).toBeGreaterThanOrEqual(3)
  const initial: Schema['ReviewItemDto'][] = await get('review')
  expect(initial.length).toBeGreaterThan(1)
  const id = initial[0].file.file.fileId
  await post(`review/${id}/skip`)
  expect((await get('review')).at(-1).file.file.fileId).toBe(id)
  const result = await post(`files/${id}/refile`, { type: 'receipt', categoryId: categories[2].id,
    title: 'Synthetic corrected receipt', receipt: { ...initial[0].file.receipt, amount: 250, date: '2026-10-21' } })
  expect(result.file.status).toBe('filed')
  expect((await get('review')).some((item: Schema['ReviewItemDto']) => item.file.file.fileId === id)).toBe(false)
  expect((await get(`files/${id}`)).file.title).toBe('Synthetic corrected receipt')
  expect((await post(`files/${id}/undo`)).success).toBe(true)
  expect((await get('review')).some((item: Schema['ReviewItemDto']) => item.file.file.fileId === id)).toBe(true)
})

test('browser activity uses limit/cursor and mutations reject missing protection headers', async () => {
  server.use(...createBrowserHandlers())
  const first = await (await fetch(`${base}/api/activity?limit=50`)).json()
  expect(first.items).toHaveLength(50)
  expect(first.nextCursor).toBeTruthy()
  const second = await (await fetch(`${base}/api/activity?limit=50&cursor=${first.nextCursor}`)).json()
  expect(second.items.length).toBeGreaterThan(0)
  expect(second.items.some((item: Schema['ActivityItemDto']) => item.fileId === first.items[0].fileId)).toBe(false)
  const id = first.items[0].fileId
  expect((await fetch(`${base}/api/files/${id}/undo`, { method: 'POST' })).status).toBe(403)
})

test('browser mocks refuse incomplete receipt conversion and keep statistics available', async () => {
  server.use(...createBrowserHandlers())
  const activity = await (await fetch(`${base}/api/activity?limit=50`)).json()
  const document = activity.items.find((item: Schema['ActivityItemDto']) => item.type === 'document')
  const result = await fetch(`${base}/api/files/${document.fileId}/refile`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-SlothBox': '1' },
    body: JSON.stringify({ type: 'receipt', categoryId: 1, receipt: null }),
  })
  expect(result.status).toBe(400)
  const stats = await fetch(`${base}/api/stats/laziness?range=week`)
  expect(stats.status).toBe(200)
  expect((await stats.json()).filesFiled).toBeGreaterThan(0)
})

test('browser search and receipt month filters use the same files as the drawer', async () => {
  server.use(...createBrowserHandlers())
  const search = await (await fetch(`${base}/api/search?q=Sample&type=document`)).json()
  expect(search.items.length).toBeGreaterThan(0)
  expect(search.items.every((hit: Schema['SearchHitDto']) => hit.file.file.type === 'document')).toBe(true)
  expect((await (await fetch(`${base}/api/search?q=unmatched-xyz`)).json()).items).toEqual([])
  const month = new Date().toLocaleDateString('en-CA').slice(0, 7)
  const ledger = await (await fetch(`${base}/api/receipts?month=${month}`)).json()
  expect(ledger.month).toBe(month)
  const empty = await (await fetch(`${base}/api/receipts?month=2000-01`)).json()
  expect(empty.items).toEqual([])
})

test('browser folder and setup mutations persist and rotation changes the QR with the URL', async () => {
  server.use(...createBrowserHandlers())
  const post = async (path: string, body?: unknown) => (await fetch(`${base}/api/${path}`, {
    method: 'POST', headers: { 'X-SlothBox': '1', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })).json()
  const added = await post('folders', { path: 'D:\\Synthetic-Input', mode: 'move', tidyExisting: false })
  expect((await (await fetch(`${base}/api/folders`)).json()).some((folder: Schema['FolderDto']) => folder.id === added.id)).toBe(true)
  expect(await post('folders/check', { path: 'D:\\' })).toMatchObject({ accepted: false })
  const phone = await (await fetch(`${base}/api/phone`)).json()
  const rotated = await post('phone/rotate')
  expect(rotated.url).not.toBe(phone.url)
  expect(rotated.qrSvg).not.toBe(phone.qrSvg)
  await post('setup/complete', { outputRoot: 'D:\\Synthetic-Filed', folders: [], finishAnyway: true })
  expect(await (await fetch(`${base}/api/status`)).json()).toMatchObject({ paused: true, setupComplete: true })
})

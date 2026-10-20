// @vitest-environment node
import { afterAll, afterEach, beforeAll, expect, test } from 'vitest'
import { server } from '../src/mocks/server'
import { createBrowserHandlers } from '../src/mocks/browser-handlers'

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

// @vitest-environment node
import { afterAll, afterEach, beforeAll, expect, test, vi } from 'vitest'
import contract from '../../../contracts/openapi.json'
import { server } from '../src/mocks/server'
import { responses } from '../src/mocks/fixtures'
import { sampleJpeg } from '../src/mocks/image'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

const fileId = '10000000-0000-4000-8000-000000000001'
const laziness = { receiptSeconds: 30, screenshotSeconds: 15, documentSeconds: 15, otherSeconds: 5 }
const bodies: Record<string, unknown> = {
  Refile: { type: 'receipt', categoryId: 1, title: 'Sample purchase', receipt: null },
  UndoSince: { since: '2026-10-20T00:00:00Z' },
  CreateCategory: { type: 'receipt', name: 'Sample', description: 'Sample receipts', folder: 'Sample' },
  UpdateCategory: { name: 'Sample', description: 'Sample receipts', folder: null },
  CheckFolder: { path: 'D:\\SlothBox-Sample\\Incoming' },
  CreateFolder: { path: 'D:\\SlothBox-Sample\\Incoming', label: 'Sample', mode: 'copy', tidyExisting: false },
  UpdateFolder: { enabled: true, mode: 'copy', label: 'Sample' },
  UpdateSettings: { startAtLogin: false, laziness, ollamaUrl: 'http://127.0.0.1:11436' },
  CompleteSetup: { outputRoot: 'D:\\SlothBox-Sample\\Filed', folders: [], finishAnyway: false },
  CheckOutputFolder: { path: 'D:\\SlothBox-Sample\\Filed' },
}
const mediaTypes: Record<string, string> = {
  GetThumbnail: 'image/jpeg', GetPreview: 'image/jpeg',
  ExportReceipts: 'text/csv', GetPhonePage: 'text/html', GetEvents: 'text/event-stream',
}
const methods = new Set(['get', 'post', 'put', 'patch', 'delete', 'head', 'options', 'trace'])
const operations = Object.entries(contract.paths).flatMap(([path, item]) =>
  Object.entries(item).filter(([method]) => methods.has(method))
    .map(([method, operation]) => ({ path, method, operationId: operation.operationId })))

test.each(operations)('$method $path has a working explicit mock', async ({ path, method, operationId }) => {
  const urlPath = path.replace('{id}', path.includes('/files/') || path.includes('/review/') ? fileId : '1')
    .replace('{token}', 'sample-token').replace('{sha256}', 'a'.repeat(64))
  const headers = new Headers({ 'X-SlothBox': '1' })
  let body: BodyInit | undefined
  if (operationId === 'UploadPhoneFile') {
    const form = new FormData()
    form.append('file', new Blob([sampleJpeg()], { type: 'image/jpeg' }), 'sample.jpg')
    body = form
  } else if (operationId in bodies) {
    body = JSON.stringify(bodies[operationId])
    headers.set('Content-Type', 'application/json')
  }
  const port = path.startsWith('/u/') ? 5081 : 5080
  const response = await fetch(`http://127.0.0.1:${port}${urlPath}`, {
    method: method.toUpperCase(), headers, body, signal: AbortSignal.timeout(3000),
  })
  expect(response.status).toBe(200)
  const mediaType = mediaTypes[operationId] ?? 'application/json'
  expect(response.headers.get('content-type')).toContain(mediaType)
  if (mediaType === 'application/json') {
    const fixture = Object.entries(responses).find(([name]) => name === operationId)?.[1]
    expect(fixture).toBeDefined()
    expect(await response.json()).toEqual(fixture)
  } else if (mediaType === 'image/jpeg') {
    const bytes = new Uint8Array(await response.arrayBuffer())
    expect([...bytes.slice(0, 2)]).toEqual([0xff, 0xd8])
  } else {
    const text = await response.text()
    expect(text.length).toBeGreaterThan(0)
    if (operationId === 'GetEvents') {
      expect(text).toContain('event: queue.changed\ndata: {"waiting":2,"processing":1}\n\n')
    }
  }
})

test('missing mock routes fail instead of contacting a live service', async () => {
  const error = vi.spyOn(console, 'error').mockImplementation(() => undefined)
  try {
    await expect(fetch('http://127.0.0.1:5080/api/missing')).rejects.toThrow()
    expect(error).toHaveBeenCalled()
  } finally {
    error.mockRestore()
  }
})

test('SSE mocks frame all eight named event payloads and close the stream', async () => {
  const response = await fetch('http://127.0.0.1:5080/events', { signal: AbortSignal.timeout(3000) })
  const frames = (await response.text()).trim().split('\n\n')
  expect(frames.map(frame => frame.split('\n')[0])).toEqual([
    'event: queue.changed', 'event: file.filed', 'event: file.review', 'event: file.duplicate',
    'event: file.undone', 'event: file.refiled', 'event: status.changed', 'event: folder.tidy-progress',
  ])
  for (const frame of frames) {
    expect(() => JSON.parse(frame.split('\ndata: ')[1])).not.toThrow()
  }
})

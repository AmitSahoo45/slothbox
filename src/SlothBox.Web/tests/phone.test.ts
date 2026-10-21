// @vitest-environment node
import { beforeAll, afterEach, beforeEach, expect, test } from 'vitest'
import { build } from 'vite'
import { JSDOM } from 'jsdom'

let html = ''
let outputs: string[] = []
let modules: string[] = []
let dom: JSDOM
const requests: UploadRequest[] = []

class UploadRequest {
  upload = new EventTarget()
  status = 0
  responseText = ''
  timeout = 0
  method = ''
  url = ''
  body?: FormData
  onload?: () => void
  onerror?: () => void
  ontimeout?: () => void
  onabort?: () => void
  open(method: string, url: string) { this.method = method; this.url = url }
  send(body: FormData) { this.body = body; requests.push(this) }
  respond(status: number, received = true) {
    this.status = status
    this.responseText = JSON.stringify({ fileName: 'received.jpg', received })
    this.onload?.()
  }
  progress(loaded: number, total: number) {
    const event = new Event('progress')
    Object.assign(event, { loaded, total, lengthComputable: true })
    this.upload.dispatchEvent(event)
  }
}

beforeAll(async () => {
  const result = await build({ logLevel: 'silent', build: { write: false } })
  const bundles = Array.isArray(result) ? result : [result]
  const assets = bundles.flatMap(bundle => 'output' in bundle ? bundle.output : [])
  outputs = assets.map(asset => asset.fileName)
  modules = assets.flatMap(asset => asset.type === 'chunk' ? Object.keys(asset.modules) : [])
  const phone = assets.find(asset => asset.fileName === 'phone/index.html')
  html = phone?.type === 'asset' ? String(phone.source) : ''
}, 60000)
beforeEach(() => {
  requests.length = 0
  dom = new JSDOM(html, {
    url: 'http://127.0.0.1:5081/u/sample-token',
    runScripts: 'dangerously',
    beforeParse(window) {
      Object.defineProperty(window, 'XMLHttpRequest', { value: UploadRequest })
    },
  })
})
afterEach(() => dom.window.close())

function choose(names = ['sample.jpg', 'bill.pdf']) {
  const input = dom.window.document.querySelector<HTMLInputElement>('#files')!
  const files = names.map(name => new dom.window.File(['synthetic'], name, {
    type: name.endsWith('.pdf') ? 'application/pdf' : 'image/jpeg',
  }))
  Object.defineProperty(input, 'files', { value: files, configurable: true })
  input.dispatchEvent(new dom.window.Event('change'))
}
const tick = () => new Promise(resolve => setTimeout(resolve, 0))
const text = () => dom.window.document.querySelector('main')?.textContent

test('one build emits the dashboard and a self-contained phone page', () => {
  expect(outputs).toContain('index.html')
  expect(outputs).toContain('phone/index.html')
  expect(dom.window.document.querySelector('h1')?.textContent).toBe('Send files to your laptop')
  expect(dom.window.document.querySelectorAll('script[src], link[href]:not([href^="data:"]), img[src]')).toHaveLength(0)
  expect(dom.window.document.querySelector('style')?.textContent).toContain('prefers-color-scheme: dark')
})
test('offers rear camera capture and multiple image or PDF selection', () => {
  const camera = dom.window.document.querySelector('#camera')
  const files = dom.window.document.querySelector('#files')
  expect(camera?.getAttribute('accept')).toBe('image/*')
  expect(camera?.getAttribute('capture')).toBe('environment')
  expect(files?.getAttribute('accept')).toBe('image/*,application/pdf')
  expect(files?.hasAttribute('multiple')).toBe(true)
})
test('production output excludes browser mocks, fixtures and worker assets', () => {
  expect(outputs.some(name => /mockServiceWorker|fixtures/i.test(name))).toBe(false)
  expect(modules.filter(name => /[/\\](msw|mocks)[/\\]/.test(name))).toEqual([])
})
test('sends one file at a time to the token upload route with per-file progress', async () => {
  choose()
  expect(requests).toHaveLength(1)
  expect(requests[0].method).toBe('POST')
  expect(requests[0].url).toBe('/u/sample-token/upload')
  expect([...requests[0].body!.keys()]).toEqual(['file'])
  expect((requests[0].body!.get('file') as File).name).toBe('sample.jpg')
  requests[0].progress(25, 100)
  expect(dom.window.document.querySelector('progress')?.value).toBe(25)
  requests[0].respond(200)
  await tick()
  expect(requests).toHaveLength(2)
  requests[1].respond(200)
  await tick()
  expect(text()).toContain('Received 2 files. You can close this page.')
  expect(text()?.match(/Sent/g)).toHaveLength(2)
})
test.each([
  [401, 'This link no longer works. Scan the new QR code on your laptop.'],
  [413, 'This file is over 50 MB.'],
  [415, 'Only images and PDFs can be sent.'],
])('explains HTTP %s without reporting success', async (status, message) => {
  choose(['sample.jpg'])
  requests[0].respond(status as number)
  await tick()
  expect(text()).toContain(message)
  expect(dom.window.document.querySelector('[role="alert"]')?.textContent).toContain(message)
  expect(text()).not.toContain('You can close this page.')
})
test('an expired token stops queued uploads and disables further selection', async () => {
  choose()
  requests[0].respond(401)
  await tick()
  expect(requests).toHaveLength(1)
  expect(dom.window.document.querySelector<HTMLInputElement>('#files')?.disabled).toBe(true)
  expect(dom.window.document.querySelector<HTMLInputElement>('#camera')?.disabled).toBe(true)
})
test('keeps the queue moving after a network error and counts only received files', async () => {
  choose()
  requests[0].onerror?.()
  await tick()
  expect(text()).toContain('Could not send this file.')
  expect(requests).toHaveLength(2)
  requests[1].respond(200)
  await tick()
  expect(text()).toContain('Received 1 file.')
  expect(text()).not.toContain('You can close this page.')
})
test('does not mark an unacknowledged file as sent or render filenames as HTML', async () => {
  choose(['<img src=x onerror=alert(1)>.jpg'])
  requests[0].respond(200, false)
  await tick()
  expect(text()).not.toContain('Sent')
  expect(dom.window.document.querySelector('img')).toBeNull()
  expect(text()).toContain('Could not send this file.')
})
test.each(['onerror', 'ontimeout', 'onabort'] as const)('announces %s failures without announcing progress ticks', async event => {
  choose(['sample.jpg'])
  requests[0].progress(25, 100)
  expect(dom.window.document.querySelector('[role="alert"]')?.textContent).toBe('')
  requests[0][event]?.()
  await tick()
  expect(dom.window.document.querySelector('[role="alert"]')?.textContent).toContain('Could not send this file.')
})

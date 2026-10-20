import { http, HttpResponse } from 'msw'
import { handlers } from './handlers'
import { status } from './fixtures'
import type { MockEvent, Schema } from '../api/types'
import { createBatch2Handlers } from './batch2-handlers'

export function createBrowserHandlers() {
  let current: Schema['StatusDto'] = structuredClone(status)
  const subscribers = new Set<ReadableStreamDefaultController<Uint8Array>>()
  const encoder = new TextEncoder()
  const statusEvent = () => encoder.encode(`event: status.changed\ndata: ${JSON.stringify(current)}\n\n`)
  const publish = (event: MockEvent) => {
    for (const subscriber of subscribers) {
      try { subscriber.enqueue(encoder.encode(`event: ${event.event}\ndata: ${JSON.stringify(event.data)}\n\n`)) }
      catch { subscribers.delete(subscriber) }
    }
  }
  const control = (paused: boolean) => ({ request }: { request: Request }) => {
    if (request.headers.get('X-SlothBox') !== '1') {
      return HttpResponse.json({ title: 'Missing X-SlothBox header', status: 403 }, { status: 403 })
    }
    current = { ...current, paused }
    for (const subscriber of subscribers) {
      try { subscriber.enqueue(statusEvent()) } catch { subscribers.delete(subscriber) }
    }
    return HttpResponse.json(current)
  }
  return [
    http.get('*/api/status', () => HttpResponse.json(current)),
    http.post('*/api/pause', control(true)),
    http.post('*/api/resume', control(false)),
    http.get('*/events', ({ request }) => {
      let timer: ReturnType<typeof setInterval>
      let subscriber: ReadableStreamDefaultController<Uint8Array>
      const clean = () => { clearInterval(timer); subscribers.delete(subscriber) }
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          subscriber = controller
          subscribers.add(controller)
          controller.enqueue(statusEvent())
          timer = setInterval(() => {
            try { controller.enqueue(encoder.encode(': keepalive\n\n')) } catch { clean() }
          }, 15000)
          request.signal.addEventListener('abort', () => {
            clean()
            try { controller.close() } catch { /* Already closed by the browser. */ }
          }, { once: true })
        },
        cancel: clean,
      })
      return new HttpResponse(stream, { headers: {
        'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache',
      } })
    }),
    ...createBatch2Handlers(publish),
    ...handlers,
  ]
}

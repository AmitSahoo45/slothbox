import { beforeAll, beforeEach, afterEach, afterAll, vi } from 'vitest'
import { server } from '../src/mocks/server'

export class TestEvents extends EventTarget {
  static latest: TestEvents
  constructor() { super(); TestEvents.latest = this }
  close = vi.fn()
  emit(name: string, data: unknown) {
    this.dispatchEvent(new MessageEvent(name, { data: JSON.stringify(data) }))
  }
}

export function setupUi(path = '/') {
  beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
  beforeEach(() => {
    window.history.replaceState({}, '', path)
    vi.stubGlobal('EventSource', TestEvents)
    vi.stubGlobal('matchMedia', () => ({
      matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn(),
    }))
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 })
    vi.stubGlobal('scrollTo', vi.fn())
  })
  afterEach(() => { server.resetHandlers(); vi.unstubAllGlobals() })
  afterAll(() => server.close())
}

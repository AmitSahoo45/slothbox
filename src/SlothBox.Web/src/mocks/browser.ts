import { setupWorker } from 'msw/browser'
import { createBrowserHandlers } from './browser-handlers'

export const worker = setupWorker(...createBrowserHandlers({ setupComplete: !new URLSearchParams(window.location.search).has('mockSetup') }))
export async function startMocks() {
  await worker.start({ quiet: true, onUnhandledFrame: 'bypass' })
}

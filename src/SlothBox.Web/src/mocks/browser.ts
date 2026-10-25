import { setupWorker } from 'msw/browser'
import { createBrowserHandlers } from './browser-handlers'

const params = new URLSearchParams(window.location.search)
const pauseReason = params.get('mockPause')
export const worker = setupWorker(...createBrowserHandlers({
  setupComplete: !params.has('mockSetup'),
  pauseReason: pauseReason === 'drive_full' || pauseReason === 'gemma_offline' ? pauseReason : null,
}))
export async function startMocks() {
  await worker.start({ quiet: true, onUnhandledFrame: 'bypass' })
}

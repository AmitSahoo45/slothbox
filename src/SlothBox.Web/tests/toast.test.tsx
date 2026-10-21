import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { ToastProvider } from '../src/components/Toast'
import { useToasts } from '../src/components/toast-context'

function Actions() {
  const { notify } = useToasts()
  return <>
    <button onClick={() => notify({ message: 'Saved' })}>Save</button>
    <button onClick={() => notify({ message: 'Try again', tone: 'error' })}>Fail</button>
  </>
}
beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())
const advance = (milliseconds: number) => act(async () => { await vi.advanceTimersByTimeAsync(milliseconds) })

test('success toast hides after six seconds', async () => {
  render(<ToastProvider><Actions /></ToastProvider>)
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))
  await advance(0)
  await advance(5999)
  expect(screen.getByText('Saved')).toBeVisible()
  await advance(1)
  expect(screen.queryByText('Saved')).not.toBeInTheDocument()
})
test('error toast persists until dismissed', async () => {
  render(<ToastProvider><Actions /></ToastProvider>)
  fireEvent.click(screen.getByRole('button', { name: 'Fail' }))
  await advance(20000)
  expect(screen.getByText('Try again')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
  expect(screen.queryByText('Try again')).not.toBeInTheDocument()
})
test('focus pauses the success timer until focus leaves', async () => {
  render(<ToastProvider><Actions /></ToastProvider>)
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))
  await advance(1000)
  act(() => screen.getByRole('button', { name: 'Dismiss notification' }).focus())
  await advance(10000)
  expect(screen.getByText('Saved')).toBeVisible()
  act(() => screen.getByRole('button', { name: 'Save' }).focus())
  await advance(5000)
  expect(screen.queryByText('Saved')).not.toBeInTheDocument()
})

import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, test } from 'vitest'
import { http, HttpResponse } from 'msw'
import { App } from '../src/App'
import { responses } from '../src/mocks/fixtures'
import { server } from '../src/mocks/server'
import { setupUi } from './ui-helpers'

setupUi('/receipts')
test('money uses en-IN grouping, currencies stay separate, and export follows the month', async () => {
  const months: string[] = []
  server.use(http.get('*/api/receipts', ({ request }) => {
    const month = new URL(request.url).searchParams.get('month')!
    months.push(month)
    return HttpResponse.json({ ...responses.GetReceipts, month,
      items: [{ ...responses.GetReceipts.items[0], amount: 123456 }],
      totals: [{ currency: 'INR', amount: 123456, count: 1 }, { currency: 'USD', amount: 20, count: 1 }],
      categories: [{ categoryId: 1, category: 'Food', currency: 'INR', amount: 123456 }],
    })
  }))
  render(<App />)
  expect(await screen.findByRole('cell', { name: '₹1,23,456.00' })).toHaveClass('receipt-amount')
  expect(screen.getByText(new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'USD' }).format(20))).toBeVisible()
  fireEvent.change(screen.getByLabelText('Month'), { target: { value: '2026-09' } })
  await waitFor(() => expect(months.at(-1)).toBe('2026-09'))
  expect(screen.getByRole('link', { name: 'Export CSV' })).toHaveAttribute('href', '/api/receipts/export.csv?month=2026-09')
  expect(screen.getByRole('link', { name: 'Sample Cafe' })).toHaveAttribute('href', expect.stringContaining('file='))
})
test('empty month offers the phone QR code', async () => {
  server.use(http.get('*/api/receipts', () => HttpResponse.json({ ...responses.GetReceipts, items: [], totals: [], categories: [] })))
  render(<App />)
  expect(await screen.findByText('No receipts this month. Snap one with the phone page.')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: 'Show phone QR code' }))
  expect(await screen.findByRole('dialog')).toBeVisible()
})

test('clicking an amount opens the receipt drawer', async () => {
  render(<App />)
  fireEvent.click(await screen.findByRole('cell', { name: '₹120.00' }))
  expect(await screen.findByRole('dialog', { name: 'File details' })).toBeVisible()
})

test('receipt API refusals show their ProblemDetails title for the ledger and the CSV export', async () => {
  let refuse = true
  const problem = () => HttpResponse.json({ title: 'Choose a month like 2026-10.', detail: 'Synthetic technical note.', status: 400 }, { status: 400 })
  server.use(
    http.get('*/api/receipts', () => refuse ? problem() : HttpResponse.json(responses.GetReceipts)),
    http.get('*/api/receipts/export.csv', problem),
  )
  render(<App />)
  expect(await screen.findByText('Choose a month like 2026-10.')).toBeVisible()
  expect(screen.queryByText('Synthetic technical note.')).not.toBeInTheDocument()
  refuse = false
  fireEvent.click(screen.getByRole('button', { name: 'Retry receipts' }))
  fireEvent.click(await screen.findByRole('link', { name: 'Export CSV' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Choose a month like 2026-10.')
})

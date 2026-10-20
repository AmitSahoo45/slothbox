import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { receiptCsv, request } from '../../api/client'
import { QueryError } from '../../components/QueryError'
import { displayDate, localDate, money } from '../../components/format'
import { PhoneQr } from '../Today/PhoneQr'

export function Receipts() {
  const [month, setMonth] = useState(localDate(new Date()).slice(0, 7))
  const [params] = useSearchParams()
  const receipts = useQuery({ queryKey: ['receipts', month], enabled: /^\d{4}-\d{2}$/.test(month),
    queryFn: ({ signal }) => request('get', '/api/receipts', { signal, query: { month } }) })
  const data = receipts.data
  const exportCsv = useMutation({ mutationFn: async (selectedMonth: string) => {
    const url = URL.createObjectURL(await receiptCsv(selectedMonth))
    const anchor = document.createElement('a')
    anchor.href = url; anchor.download = `receipts-${selectedMonth}.csv`
    document.body.append(anchor); anchor.click(); anchor.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  } })
  const currencies = [...new Set(data?.categories.map(item => item.currency) ?? [])]
  return <section className="content-page receipts-page" aria-label="Receipt ledger">
    <div className="page-toolbar"><label className="field">Month<input type="month" value={month} onChange={event => setMonth(event.target.value)} /></label>
      {month && <a className="button" href={`/api/receipts/export.csv?month=${encodeURIComponent(month)}`} download aria-disabled={exportCsv.isPending} onClick={event => {
        event.preventDefault(); if (!exportCsv.isPending) exportCsv.mutate(month)
      }}>{exportCsv.isPending ? 'Exporting…' : 'Export CSV'}</a>}</div>
    {exportCsv.error && <p className="query-error" role="alert">{exportCsv.error.message}</p>}
    {receipts.isFetching && <p role="status">Loading receipts…</p>}
    {receipts.error && <QueryError error={receipts.error} retry={() => { void receipts.refetch() }} label="Retry receipts" />}
    {data && (data.items.length ? <>
      <div className="receipt-totals">{data.totals.map(total => <p key={total.currency}>Spent this month: <strong>{money(total.amount, total.currency)}</strong> across {total.count} {Number(total.count) === 1 ? 'receipt' : 'receipts'}.</p>)}</div>
      <div className="receipt-charts">{currencies.map(currency => {
        const categories = data.categories.filter(item => item.currency === currency).sort((a, b) => Number(b.amount) - Number(a.amount))
        const maximum = Math.max(1, ...categories.map(item => Number(item.amount)))
        return <section key={currency} aria-label={`${currency} by category`}><h2>{currency}</h2>
          {categories.map(category => <div className="receipt-bar-row" key={category.categoryId ?? category.category}>
            <span>{category.category}</span><span className="receipt-bar-track" aria-hidden="true"><span style={{ width: `${Math.max(0, Number(category.amount)) / maximum * 100}%` }} /></span>
            <span className="receipt-amount">{money(category.amount, currency)}</span>
          </div>)}
        </section>
      })}</div>
      <div className="table-scroll" role="region" aria-label="Receipts table" tabIndex={0}><table className="receipts-table">
        <thead><tr><th>Date</th><th>Vendor</th><th>Category</th><th>Paid by</th><th className="receipt-amount">Amount</th></tr></thead>
        <tbody>{data.items.map(item => {
          const next = new URLSearchParams(params); next.set('file', item.fileId)
          return <tr key={item.fileId} onClick={event => {
            if ((event.target as HTMLElement).closest('a')) return
            const link = event.currentTarget.querySelector('a'); link?.focus(); link?.click()
          }}><td>{item.date ? displayDate(item.date) : 'Unknown date'}</td>
            <td><Link to={`?${next}`}>{item.vendor || 'Unknown vendor'}</Link></td><td>{item.category}</td>
            <td>{item.paymentMethod === 'upi' ? 'UPI' : item.paymentMethod === 'unknown' ? 'Unknown' : item.paymentMethod.charAt(0).toUpperCase() + item.paymentMethod.slice(1)}</td>
            <td className="receipt-amount">{item.amount != null && item.currency ? money(item.amount, item.currency) : 'Not recorded'}</td></tr>
        })}</tbody>
      </table></div>
    </> : <div className="empty-state"><p>No receipts this month. Snap one with the phone page.</p><PhoneQr /></div>)}
  </section>
}

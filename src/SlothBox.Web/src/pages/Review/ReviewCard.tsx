import { useEffect, useId, useRef, useState } from 'react'
import type { Schema } from '../../api/types'
import { useFileActions } from '../../api/file-actions'
import { Button } from '../../components/Button'
import { CategoryChoices } from '../../components/CategoryChoices'
import { Thumbnail } from '../../components/Thumbnail'
import { originLabel, relativeTime, typeNames } from '../../components/format'

const reasons: Record<NonNullable<Schema['ReviewReason']>, string> = {
  missing_date: 'No date found on this receipt.',
  missing_amount: 'No amount found on this receipt.',
  unknown_type: "Gemma couldn't tell what this is.",
  bad_output: "Gemma's answer was unreadable twice.",
  heic: "HEIC photos aren't supported yet.",
  scanned_pdf: 'This PDF has no text layer. It looks scanned.',
  password_protected: 'This file is password-protected.',
}
export function ReviewCard({ item, categories, onFiled, onSkip, skipping, arriving }: {
  item: Schema['ReviewItemDto']; categories: Schema['CategoryDto'][]
  onFiled: (id: string) => void; onSkip: () => void; skipping: boolean; arriving: boolean
}) {
  const details = item.file
  const formId = useId()
  const file = details.file
  const [type, setType] = useState(file.type)
  const [category, setCategory] = useState(String(details.categoryId ?? ''))
  const [title, setTitle] = useState(file.title)
  const [vendor, setVendor] = useState(details.receipt?.vendor ?? '')
  const [date, setDate] = useState(details.receipt?.date ?? '')
  const [amount, setAmount] = useState(String(details.receipt?.amount ?? ''))
  const [currency, setCurrency] = useState(details.receipt?.currency ?? '')
  const [paidBy, setPaidBy] = useState<Schema['PaymentMethod']>(details.receipt?.paymentMethod ?? 'unknown')
  const { refile, open } = useFileActions()
  const form = useRef<HTMLFormElement>(null)
  const heading = useRef<HTMLParagraphElement>(null)
  const choices = categories.filter(category => category.type === type)
  const selected = choices.find(choice => String(choice.id) === category)
  const parsedDate = new Date(`${date}T00:00:00Z`)
  const validDate = /^\d{4}-\d{2}-\d{2}$/.test(date) && Number.isFinite(parsedDate.getTime()) && parsedDate.toISOString().slice(0, 10) === date
  const validReceipt = validDate && amount.trim() !== '' && Number.isFinite(Number(amount)) && (!currency || /^[A-Z]{3}$/.test(currency))
  const canFile = type !== 'unknown' && !!selected && (type !== 'receipt' || validReceipt)
  const busy = refile.isPending || skipping
  useEffect(() => { heading.current?.focus() }, [])
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null
      if (busy || event.repeat || event.ctrlKey || event.metaKey || event.altKey ||
        target?.isContentEditable || target?.closest?.('input, textarea, select, [role="dialog"], [role="menu"]')) return
      const number = Number(event.key)
      if (/^[1-9]$/.test(event.key) && choices[number - 1]) {
        event.preventDefault(); setCategory(String(choices[number - 1].id))
      } else if (event.key === 'Enter' && !target?.closest?.('button, a')) {
        event.preventDefault()
        if (canFile) form.current?.requestSubmit()
      } else if (event.key.toLowerCase() === 's') {
        event.preventDefault(); onSkip()
      } else if (event.key.toLowerCase() === 'o') {
        event.preventDefault(); open.mutate({ id: file.fileId, action: 'open' })
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [busy, canFile, choices, file.fileId, onSkip, open])
  return <div className="review-card" data-arriving={arriving}>
    <div className="review-preview">
      <Thumbnail file={file} preview />
      <p className="literal">{details.originalName}</p>
      <p className="muted">from {originLabel(file)}, {relativeTime(file.at)}</p>
    </div>
    <form ref={form} onSubmit={event => {
      event.preventDefault()
      if (!canFile || busy || !selected) return
      refile.mutate({ id: file.fileId, body: {
        type, categoryId: selected.id, title,
        receipt: type === 'receipt' ? { vendor: vendor.trim() || null, date, amount: Number(amount),
          currency: currency || null, paymentMethod: paidBy } : null,
      } }, { onSuccess: () => onFiled(file.fileId) })
    }}>
      <p className="review-reason" ref={heading} tabIndex={-1}>{item.reason ? reasons[item.reason] : 'Choose where this file belongs.'}</p>
      <fieldset disabled={busy} className="review-fields">
        <div className="field"><label htmlFor={`${formId}-type`}>Type</label><select id={`${formId}-type`} value={type} onChange={event => { setType(event.target.value as Schema['FileKind']); setCategory('') }}>
          {Object.entries(typeNames).map(([value, label]) => <option value={value} key={value} disabled={value === 'unknown'}>{label}</option>)}
        </select></div>
        <CategoryChoices categories={choices} value={category} onChange={setCategory} disabled={busy} />
        {type === 'receipt' && <>
          <label className="field">Vendor<input value={vendor} onChange={event => setVendor(event.target.value)} /></label>
          <label className="field">Date<input type="date" required value={date} onChange={event => setDate(event.target.value)} /></label>
          <div className="amount-fields">
            <label className="field">Amount<input type="number" step="any" required value={amount} onChange={event => setAmount(event.target.value)} /></label>
            <label className="field">Currency<input list="receipt-currencies" maxLength={3} value={currency}
              onChange={event => setCurrency(event.target.value.toUpperCase())} /></label>
            <datalist id="receipt-currencies"><option value="INR" /><option value="USD" /><option value="EUR" /><option value="GBP" /></datalist>
          </div>
          <div className="field"><label htmlFor={`${formId}-payment`}>Paid by</label><select id={`${formId}-payment`} value={paidBy} onChange={event => setPaidBy(event.target.value as Schema['PaymentMethod'])}>
            <option value="unknown">Unknown</option><option value="upi">UPI</option><option value="card">Card</option>
            <option value="cash">Cash</option><option value="netbanking">Net banking</option>
          </select></div>
        </>}
        <label className="field">Title<input value={title} onChange={event => setTitle(event.target.value)} /></label>
      </fieldset>
      <div className="form-actions">
        <Button type="submit" variant="primary" disabled={!canFile || skipping} busy={refile.isPending}>File it</Button>
        <Button disabled={refile.isPending} busy={skipping} onClick={onSkip}>Skip for now</Button>
        <Button busy={open.isPending} onClick={() => open.mutate({ id: file.fileId, action: 'open' })}>Open</Button>
      </div>
      <p className="shortcut-hints">1–9 pick a category. Enter file it. S skip. O open.</p>
    </form>
  </div>
}

import { useEffect, useRef, useState } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { X } from 'lucide-react'
import { request } from '../../api/client'
import { useFileActions } from '../../api/file-actions'
import type { Schema } from '../../api/types'
import { Button } from '../Button'
import { QueryError } from '../QueryError'
import { Thumbnail } from '../Thumbnail'
import { displayDate, displayTime, money, statusNames, typeNames } from '../format'
import { ChangeFile } from './ChangeFile'
import { ToastHost } from '../ToastHost'

function Details({ details }: { details: Schema['FileDetailsDto'] }) {
  const [changing, setChanging] = useState(false)
  const { open, undo } = useFileActions()
  const file = details.file
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement
      if (event.key.toLowerCase() !== 'o' || event.repeat || event.ctrlKey || event.altKey || event.metaKey ||
        target.isContentEditable || target.closest?.('input, select, textarea, [role="menu"]')) return
      event.preventDefault(); open.mutate({ id: file.fileId, action: 'open' })
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [file.fileId, open])
  return <>
    <Thumbnail file={file} preview />
    <div className="drawer-title"><h3>{file.title || file.newName}</h3><span className={`chip status-${file.status}`}>{statusNames[file.status]}</span></div>
    <p className="literal">{file.newName}</p>
    <div className="form-actions">
      <Button busy={open.isPending} onClick={() => open.mutate({ id: file.fileId, action: 'open' })}>Open</Button>
      <Button busy={open.isPending} onClick={() => open.mutate({ id: file.fileId, action: 'reveal' })}>Show in folder</Button>
      <Button busy={undo.isPending} disabled={!file.canUndo} onClick={() => undo.mutate(file.fileId)}>Undo</Button>
    </div>
    <div className="drawer-category"><p>{typeNames[file.type]} / {file.category}</p><Button onClick={() => setChanging(true)}>Change</Button></div>
    {changing && <ChangeFile details={details} onClose={() => setChanging(false)} />}
    {details.receipt && <dl className="receipt-details">
      <dt>Vendor</dt><dd>{details.receipt.vendor || 'Not found'}</dd>
      <dt>Date</dt><dd>{details.receipt.date ? displayDate(details.receipt.date) : 'Not found'}</dd>
      <dt>Amount</dt><dd className="amount">{details.receipt.amount != null
        ? details.receipt.currency ? money(details.receipt.amount, details.receipt.currency) : details.receipt.amount : 'Not found'}</dd>
      <dt>Paid by</dt><dd>{{ upi: 'UPI', card: 'Card', cash: 'Cash', netbanking: 'Net banking', unknown: 'Unknown' }[details.receipt.paymentMethod]}</dd>
    </dl>}
    <p className="file-caption">{details.caption}</p>
    <section className="file-history"><h3>History</h3>
      {details.history.length === 0 && <p className="muted">No actions recorded.</p>}
      <ol>{details.history.map(action => <li key={action.id}>
        <time dateTime={action.at}>{new Date(action.at).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}, {displayTime(action.at)}</time>
        <p>{action.kind === 'copy' ? 'Copy from' : action.kind === 'undo_move' ? 'Restore from' : action.kind === 'delete_copy' ? 'Remove filed copy' : 'Move from'}</p>
        <code>{action.fromPath}</code><p>{action.kind === 'undo_move' ? 'Restore to' : action.kind === 'delete_copy' ? 'Original kept at' : 'File in'}</p><code>{action.toPath}</code>
        <p className={action.error ? 'error-text' : 'muted'}>{action.error || action.status}</p>
      </li>)}</ol>
    </section>
  </>
}

export function FileDrawer() {
  const [params, setParams] = useSearchParams()
  const id = params.get('file')
  const returnFocus = useRef<HTMLElement | null>(null)
  const file = useQuery({ queryKey: ['files', id], enabled: !!id,
    queryFn: ({ signal }) => request('get', '/api/files/{id}', { path: { id: id! }, signal }) })
  const close = () => { const next = new URLSearchParams(params); next.delete('file'); setParams(next) }
  return <Dialog.Root open={!!id} onOpenChange={open => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="file-drawer" onOpenAutoFocus={() => {
        const active = document.activeElement
        returnFocus.current = active instanceof HTMLElement && active !== document.body ? active : null
      }} onCloseAutoFocus={event => {
        event.preventDefault()
        const target = returnFocus.current?.isConnected ? returnFocus.current : document.getElementById('page-title')
        target?.focus()
      }}>
        <div className="dialog-heading"><Dialog.Title>File details</Dialog.Title>
          <Dialog.Close asChild><Button variant="quiet" data-dialog-dismiss aria-label="Close file details"><X size={20} strokeWidth={1.75} aria-hidden="true" /></Button></Dialog.Close>
        </div>
        <Dialog.Description className="sr-only">Preview, filing details and action history.</Dialog.Description>
        {file.isPending && <p>Loading file…</p>}
        {file.error && <QueryError error={file.error} retry={() => { void file.refetch() }} label="Retry file" />}
        {file.data && <Details key={id} details={file.data} />}
        <ToastHost />
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>
}

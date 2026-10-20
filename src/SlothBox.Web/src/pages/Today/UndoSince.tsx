import { useRef, useState } from 'react'
import * as Popover from '@radix-ui/react-popover'
import * as Dialog from '@radix-ui/react-dialog'
import { useMutation } from '@tanstack/react-query'
import { request } from '../../api/client'
import { useFileActions } from '../../api/file-actions'
import { Button } from '../../components/Button'
import { useToasts } from '../../components/toast-context'
import { displayTime, localDate } from '../../components/format'
import { ToastHost } from '../../components/ToastHost'

export function UndoSince() {
  const [menu, setMenu] = useState(false)
  const [since, setSince] = useState<Date | null>(null)
  const [custom, setCustom] = useState('')
  const [pick, setPick] = useState(false)
  const [openedAt, setOpenedAt] = useState(() => Date.now())
  const trigger = useRef<HTMLButtonElement>(null)
  const { notify } = useToasts()
  const { refresh } = useFileActions()
  const undo = useMutation({
    mutationFn: (date: Date) => request('post', '/api/undo/since', { body: { since: date.toISOString() } }),
    onSuccess: result => {
      setSince(null)
      refresh()
      notify({ message: `Undone ${result.undone} ${Number(result.undone) === 1 ? 'action' : 'actions'}` })
      const failed = result.results.filter(item => !item.success)
      if (failed.length) notify({ tone: 'error', message: `${failed.length} ${failed.length === 1 ? 'action could' : 'actions could'} not be undone. ${Array.from(new Set(failed.map(item => item.reason || 'Try again.'))).join(' ')}` })
    },
    onError: error => notify({ tone: 'error', message: error.message }),
  })
  const choose = (date: Date) => { setMenu(false); setSince(date) }
  const validCustom = custom && Number.isFinite(new Date(custom).getTime()) && new Date(custom).getTime() <= openedAt
  return <>
    <Popover.Root open={menu} onOpenChange={open => { setMenu(open); if (open) setOpenedAt(Date.now()) }}>
      <Popover.Trigger asChild><Button ref={trigger}>Undo since…</Button></Popover.Trigger>
      <Popover.Portal><Popover.Content className="popover undo-options" sideOffset={8} align="end"
        aria-label="Choose undo time" onCloseAutoFocus={event => { if (since) event.preventDefault() }}>
        <Button variant="quiet" onClick={() => choose(new Date(Date.now() - 15 * 60000))}>Last 15 minutes</Button>
        <Button variant="quiet" onClick={() => choose(new Date(Date.now() - 60 * 60000))}>Last hour</Button>
        <Button variant="quiet" onClick={() => choose(new Date(`${localDate(new Date())}T00:00:00`))}>Today</Button>
        <Button variant="quiet" onClick={() => setPick(true)}>Pick a time</Button>
        {pick && <><label className="field">Since<input type="datetime-local" value={custom} onChange={event => setCustom(event.target.value)} /></label>
          <Button disabled={!validCustom} onClick={() => choose(new Date(custom))}>Continue</Button></>}
      </Popover.Content></Popover.Portal>
    </Popover.Root>
    <Dialog.Root open={!!since} onOpenChange={open => { if (!open && !undo.isPending) setSince(null) }}>
      <Dialog.Portal><Dialog.Overlay className="dialog-overlay" /><Dialog.Content className="dialog-content"
        onCloseAutoFocus={event => { event.preventDefault(); trigger.current?.focus() }}>
        <Dialog.Title>Undo actions since {since && displayTime(since.toISOString())}?</Dialog.Title>
        <Dialog.Description>Actions since {since?.toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short', hour12: false })} will be undone, newest first.</Dialog.Description>
        <div className="form-actions">
          <Button variant="destructive" busy={undo.isPending} onClick={() => { if (since) undo.mutate(since) }}>Undo actions</Button>
          <Dialog.Close asChild><Button data-dialog-dismiss disabled={undo.isPending}>Cancel</Button></Dialog.Close>
        </div>
        <ToastHost />
      </Dialog.Content></Dialog.Portal>
    </Dialog.Root>
  </>
}

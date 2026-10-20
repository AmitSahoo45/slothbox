import { useState, type ReactNode, type RefObject } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { Button } from './Button'
import { ToastHost } from './ToastHost'

export function Modal({ title, description, onClose, busy = false, children, returnFocus }: {
  title: string; description: string; onClose: () => void; busy?: boolean; children: ReactNode
  returnFocus?: RefObject<HTMLElement | null>
}) {
  const [opener] = useState(() => document.activeElement as HTMLElement | null)
  return <Dialog.Root open onOpenChange={open => { if (!open && !busy) onClose() }}>
    <Dialog.Portal><Dialog.Overlay className="dialog-overlay" /><Dialog.Content className="dialog-content" onCloseAutoFocus={event => {
      event.preventDefault()
      const target = returnFocus?.current ?? opener
      if (target?.isConnected) target.focus()
      else document.getElementById('page-title')?.focus()
    }}>
      <Dialog.Title>{title}</Dialog.Title><Dialog.Description>{description}</Dialog.Description>
      {children}
      <div className="form-actions"><Button data-dialog-dismiss disabled={busy} onClick={onClose}>Cancel</Button></div>
      <ToastHost />
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>
}

import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import * as Dialog from '@radix-ui/react-dialog'
import { Keyboard, X } from 'lucide-react'
import { Button } from '../components/Button'
import { ToastHost } from '../components/ToastHost'

export function Shortcuts() {
  const [open, setOpen] = useState(false)
  const returnFocus = useRef<HTMLElement | null>(null)
  const navigate = useNavigate()
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target
      if (event.ctrlKey || event.metaKey || event.altKey || event.repeat || open ||
        (target instanceof HTMLElement && (target.isContentEditable ||
          target.closest('input, textarea, select, [role="textbox"], [role="dialog"]')))) return
      if (event.key === '?') {
        event.preventDefault()
        returnFocus.current = document.activeElement as HTMLElement
        setOpen(true)
      } else if (event.key === '/') {
        event.preventDefault()
        navigate('/search')
        requestAnimationFrame(() => {
          (document.querySelector<HTMLElement>('[data-search-input]') ?? document.getElementById('page-title'))?.focus()
        })
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [navigate, open])
  return <Dialog.Root open={open} onOpenChange={next => {
    if (next) returnFocus.current = document.activeElement as HTMLElement
    setOpen(next)
  }}>
    <Dialog.Trigger asChild><Button variant="quiet" aria-label="Keyboard shortcuts">
      <Keyboard aria-hidden="true" size={20} strokeWidth={1.75} /><span className="shortcut-label">Shortcuts</span>
    </Button></Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="dialog-content" onCloseAutoFocus={event => {
        if (returnFocus.current?.isConnected) { event.preventDefault(); returnFocus.current.focus() }
      }}>
        <div className="dialog-heading">
          <Dialog.Title>Keyboard shortcuts</Dialog.Title>
          <Dialog.Close asChild><Button variant="quiet" data-dialog-dismiss aria-label="Close shortcuts">
            <X aria-hidden="true" size={20} strokeWidth={1.75} />
          </Button></Dialog.Close>
        </div>
        <Dialog.Description>Shortcuts are available outside text fields.</Dialog.Description>
        <dl className="shortcuts-list">
          <dt><kbd>/</kbd></dt><dd>Go to Search</dd>
          <dt><kbd>?</kbd></dt><dd>Show keyboard shortcuts</dd>
          <dt><kbd>Esc</kbd></dt><dd>Close a drawer or dialog</dd>
          <dt><kbd>F8</kbd></dt><dd>Go to notifications</dd>
          <dt><kbd>1–9</kbd></dt><dd>Pick a category in Needs review</dd>
          <dt><kbd>Enter</kbd></dt><dd>File the current review item</dd>
          <dt><kbd>S</kbd></dt><dd>Skip the current review item</dd>
          <dt><kbd>O</kbd></dt><dd>Open a file in Needs review or the drawer</dd>
        </dl>
        <ToastHost />
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>
}

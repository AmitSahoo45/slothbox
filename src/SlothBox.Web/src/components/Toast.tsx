import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react'
import * as Toast from '@radix-ui/react-toast'
import { X } from 'lucide-react'
import { ToastContext, type ToastMessage } from './toast-context'
import { Button } from './Button'

export function ToastProvider({ children }: { children: ReactNode }) {
  const nextId = useRef(0)
  const [messages, setMessages] = useState<(ToastMessage & { id: number })[]>([])
  const notify = useCallback((message: ToastMessage) => {
    const id = ++nextId.current
    setMessages(previous => [...previous, { ...message, id }])
  }, [])
  const context = useMemo(() => ({ notify }), [notify])
  return <ToastContext.Provider value={context}>
    <Toast.Provider duration={6000} swipeDirection="left">
      {children}
      {messages.map(message => <Toast.Root key={message.id}
        className={`toast toast-${message.tone ?? 'success'}`}
        duration={message.tone === 'error' ? Infinity : 6000}
        onOpenChange={open => { if (!open) setMessages(previous => previous.filter(item => item.id !== message.id)) }}>
        <Toast.Description>{message.message}</Toast.Description>
        {message.action && <Toast.Action asChild altText={message.action.altText}>
          <Button variant="quiet" onClick={message.action.onClick}>{message.action.label}</Button>
        </Toast.Action>}
        <Toast.Close asChild><Button variant="quiet" aria-label="Dismiss notification">
          <X aria-hidden="true" size={20} strokeWidth={1.75} />
        </Button></Toast.Close>
      </Toast.Root>)}
      <Toast.Viewport className="toast-viewport" label="Notifications (F8)" />
    </Toast.Provider>
  </ToastContext.Provider>
}

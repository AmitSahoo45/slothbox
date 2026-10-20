import { createContext, useContext } from 'react'

export interface ToastMessage {
  message: string
  tone?: 'success' | 'error'
  action?: { label: string; altText: string; onClick: () => void }
}
export const ToastContext = createContext<{
  notify: (message: ToastMessage) => void
  setContainer: (container: HTMLElement | null) => void
} | null>(null)
export function useToasts() {
  const value = useContext(ToastContext)
  if (!value) throw new Error('Toasts require ToastProvider')
  return value
}

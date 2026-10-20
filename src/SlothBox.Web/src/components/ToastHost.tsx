import { useToasts } from './toast-context'

// Keep notifications inside the active dialog's focus and accessibility boundary.
export function ToastHost() {
  const { setContainer } = useToasts()
  return <div ref={setContainer} className="toast-host" />
}

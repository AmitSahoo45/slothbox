import { Button } from './Button'

export function QueryError({ error, retry, label }: { error: Error; retry: () => void; label: string }) {
  return <div className="query-error" role="alert">
    <p>{error.message}</p><Button onClick={retry}>{label}</Button>
  </div>
}

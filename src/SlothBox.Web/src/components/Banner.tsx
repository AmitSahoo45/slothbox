import type { ReactNode } from 'react'
import { AlertCircle, Info } from 'lucide-react'

export function Banner({ tone = 'attention', children, action }:
  { tone?: 'attention' | 'error' | 'neutral' | 'info'; children: ReactNode; action?: ReactNode }) {
  const Icon = tone === 'error' ? AlertCircle : Info
  return <div className={`banner banner-${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
    <Icon aria-hidden="true" size={20} strokeWidth={1.75} />
    <div className="banner-copy">{children}</div>
    {action}
  </div>
}

import type { ButtonHTMLAttributes } from 'react'

export function Button({ className = '', busy, variant = 'secondary', disabled, ...props }:
  ButtonHTMLAttributes<HTMLButtonElement> & { busy?: boolean; variant?: 'primary' | 'secondary' | 'quiet' | 'destructive' }) {
  return <button type="button" {...props} className={`button button-${variant} ${className}`}
    disabled={disabled || busy} aria-busy={busy || undefined} />
}

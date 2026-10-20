import type { ComponentPropsWithRef } from 'react'

export function Button({ className = '', busy, variant = 'secondary', disabled, ...props }:
  ComponentPropsWithRef<'button'> & { busy?: boolean; variant?: 'primary' | 'secondary' | 'quiet' | 'destructive' }) {
  return <button type="button" {...props} className={`button button-${variant} ${className}`}
    disabled={disabled || busy} aria-busy={busy || undefined} />
}

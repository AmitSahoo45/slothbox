import { useState } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useQuery } from '@tanstack/react-query'
import { request } from '../../api/client'
import { Button } from '../../components/Button'
import { QueryError } from '../../components/QueryError'
import { ToastHost } from '../../components/ToastHost'
import { phoneUnavailable } from '../../components/format'

export function PhoneQr() {
  const [open, setOpen] = useState(false)
  const phone = useQuery({ queryKey: ['phone'], enabled: open,
    queryFn: ({ signal }) => request('get', '/api/phone', { signal }) })
  return <Dialog.Root open={open} onOpenChange={setOpen}>
    <Dialog.Trigger asChild><Button>Show phone QR code</Button></Dialog.Trigger>
    <Dialog.Portal><Dialog.Overlay className="dialog-overlay" /><Dialog.Content className="dialog-content">
      <Dialog.Title>Send files from your phone</Dialog.Title>
      <Dialog.Description>Connect your phone to the same Wi-Fi, then scan this code.</Dialog.Description>
      {phone.isPending && <p>Loading the phone link…</p>}
      {phone.error && <QueryError error={phone.error} retry={() => { void phone.refetch() }} label="Retry phone link" />}
      {phone.data && (phone.data.enabled && phone.data.qrSvg && phone.data.url
        ? <><img className="phone-qr" alt="Phone upload QR code" src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(phone.data.qrSvg)}`} />
          <p className="phone-url">{phone.data.url}</p></>
        : <p>{phoneUnavailable(phone.data)}</p>)}
      <div className="form-actions"><Dialog.Close asChild><Button data-dialog-dismiss>Close</Button></Dialog.Close></div>
      <ToastHost />
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>
}

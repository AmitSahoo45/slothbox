import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { request } from '../../api/client'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { QueryError } from '../../components/QueryError'
import { useToasts } from '../../components/toast-context'

export function PhoneBlock() {
  const phone = useQuery({ queryKey: ['phone'], queryFn: ({ signal }) => request('get', '/api/phone', { signal }) })
  const [confirm, setConfirm] = useState(false)
  const [dismissed, setDismissed] = useState(() => {
    try { return sessionStorage.getItem('slothbox-firewall-hint') === 'dismissed' } catch { return false }
  })
  const { notify } = useToasts()
  const client = useQueryClient()
  const rotate = useMutation({ mutationFn: () => request('post', '/api/phone/rotate'), onSuccess: async result => {
    await client.cancelQueries({ queryKey: ['phone'] })
    client.setQueryData(['phone'], result)
    setConfirm(false); notify({ message: 'New link ready' })
  } })
  const copy = async () => {
    if (!phone.data?.url) return
    try { await navigator.clipboard.writeText(phone.data.url); notify({ message: 'Link copied' }) }
    catch { notify({ message: 'Could not copy the link. Select it and copy it yourself.', tone: 'error' }) }
  }
  return <section className="phone-block"><h2>Phone page</h2>
    {phone.isPending && <p role="status">Loading the phone link…</p>}
    {phone.error && <QueryError error={phone.error} retry={() => { void phone.refetch() }} label="Retry phone link" />}
    {phone.data && (phone.data.enabled && phone.data.url ? <>
      <div className="phone-block-content">
        {phone.data.qrSvg && <img className="phone-qr" alt="Phone upload QR code" src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(phone.data.qrSvg)}`} />}
        <div><p>Scan with your phone on the same Wi-Fi.</p><p className="phone-url">{phone.data.url}</p>
          <div className="form-actions"><Button onClick={() => void copy()}>Copy link</Button><Button onClick={() => setConfirm(true)}>New link</Button></div></div>
      </div>
      {!phone.data.firewallRulePresent && !dismissed && <div className="banner banner-attention"><p className="banner-copy">The phone firewall rule is missing. Allow SlothBox through the firewall on your private network to use the phone page.</p>
        <Button onClick={() => { setDismissed(true); try { sessionStorage.setItem('slothbox-firewall-hint', 'dismissed') } catch { /* This session still remembers the dismissal. */ } }}>Dismiss</Button></div>}
    </> : <p>The phone page is unavailable.</p>)}
    {confirm && <Modal title="Create a new phone link?" description="The old link will stop working." onClose={() => setConfirm(false)} busy={rotate.isPending}>
      {rotate.error && <p role="alert" className="query-error">{rotate.error.message}</p>}
      <div className="form-actions"><Button variant="primary" busy={rotate.isPending} onClick={() => rotate.mutate()}>Create new link</Button></div>
    </Modal>}
  </section>
}

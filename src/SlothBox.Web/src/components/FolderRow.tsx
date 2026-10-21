import { useRef, useState } from 'react'
import * as Menu from '@radix-ui/react-dropdown-menu'
import { skipToken, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MoreHorizontal } from 'lucide-react'
import { request } from '../api/client'
import { requireSuccess } from '../api/file-actions'
import type { Schema } from '../api/types'
import { Button } from './Button'
import { Modal } from './Modal'
import { useToasts } from './toast-context'

export function FolderRow({ folder }: { folder: Schema['FolderDto'] }) {
  const client = useQueryClient()
  const { notify } = useToasts()
  const [dialog, setDialog] = useState<'tidy' | 'rename' | 'remove' | null>(null)
  const [label, setLabel] = useState(folder.label)
  const menuButton = useRef<HTMLButtonElement>(null)
  const [fromMenu, setFromMenu] = useState(false)
  const progressKey = ['folder-progress', folder.id]
  const progress = useQuery<Schema['FolderTidyProgressEvent']>({ queryKey: progressKey, queryFn: skipToken })
  const tidying = Boolean(progress.data && Number(progress.data.done) < Number(progress.data.total))
  const refresh = () => client.invalidateQueries({ queryKey: ['folders'] })
  const failure = (error: Error) => notify({ message: error.message, tone: 'error' })
  const update = useMutation({ mutationFn: (body: Schema['UpdateFolderRequest']) => request('patch', '/api/folders/{id}', { path: { id: Number(folder.id) }, body }),
    onSuccess: async () => { setDialog(null); await refresh() }, onError: failure })
  const remove = useMutation({ mutationFn: async () => requireSuccess(await request('delete', '/api/folders/{id}', { path: { id: Number(folder.id) } })),
    onSuccess: async () => { setDialog(null); await refresh(); notify({ message: 'Stopped watching folder' }) }, onError: failure })
  const tidy = useMutation({ mutationFn: () => request('post', '/api/folders/{id}/tidy-existing', { path: { id: Number(folder.id) } }),
    onMutate: () => { client.setQueryData(progressKey, { folderId: folder.id, done: 0, total: folder.baselineCount }) },
    onSuccess: result => {
      client.setQueryData<Schema['FolderTidyProgressEvent']>(progressKey, previous => ({ folderId: folder.id, done: previous?.done ?? 0, total: result.queued }))
      setDialog(null); void refresh()
    }, onError: error => { client.removeQueries({ queryKey: progressKey }); failure(error) } })
  const busy = update.isPending || remove.isPending || tidy.isPending
  return <article className="folder-row" aria-label={folder.label}>
    <div className="folder-main">
      <input type="checkbox" role="switch" aria-label={`Watch ${folder.label}`} checked={folder.enabled} disabled={busy || folder.missing} onChange={event => update.mutate({ enabled: event.target.checked })} />
      <div className="folder-description"><h3>{folder.label}</h3><code>{folder.path}</code>
        {folder.missing ? <p className="folder-missing">Folder not found</p> : <>
          <p>{folder.filedCount} filed</p>
          {Number(folder.baselineCount) > 0 && <p>{folder.baselineCount} files were already here.</p>}
          {progress.data && <p role="status">{tidying ? `Tidying ${progress.data.done} of ${progress.data.total}` : `Tidied ${progress.data.done} files`}</p>}
        </>}
      </div>
      <div className="folder-controls">
        <div className="field"><label htmlFor={`folder-mode-${folder.id}`} className="sr-only">Mode for {folder.label}</label><select id={`folder-mode-${folder.id}`} value={folder.mode} disabled={folder.isCloud || busy || folder.missing} onChange={event => update.mutate({ mode: event.target.value as Schema['FolderMode'] })}>
          <option value="move">Move</option><option value="copy">Copy only</option>
        </select></div>
        {folder.isCloud && <span className="field-hint">Copy only (cloud folder)</span>}
        {folder.missing ? <Button aria-label={`Remove ${folder.label}`} onClick={() => setDialog('remove')}>Remove</Button> : <>
          {Number(folder.baselineCount) > 0 && <Button disabled={busy || tidying} onClick={() => { setFromMenu(false); setDialog('tidy') }}>Tidy them now</Button>}
          <Menu.Root><Menu.Trigger asChild><Button ref={menuButton} aria-label={`More for ${folder.label}`} disabled={busy}><MoreHorizontal size={18} /></Button></Menu.Trigger>
            <Menu.Portal><Menu.Content className="menu" align="end">
              <Menu.Item className="menu-item" onSelect={() => { setFromMenu(true); setLabel(folder.label); setDialog('rename') }}>Rename label</Menu.Item>
              <Menu.Item className="menu-item" onSelect={() => { setFromMenu(true); setDialog('remove') }}>Stop watching</Menu.Item>
            </Menu.Content></Menu.Portal>
          </Menu.Root>
        </>}
      </div>
    </div>
    {dialog && <Modal title={dialog === 'tidy' ? `Tidy ${folder.baselineCount} files?` : dialog === 'rename' ? 'Rename label' : `Stop watching ${folder.label}?`}
      description={dialog === 'tidy' ? 'You can undo this with Undo since on Today.' : dialog === 'rename' ? 'This changes the label in SlothBox.' : 'Already-filed files will stay where they are.'} onClose={() => { setDialog(null); setFromMenu(false) }} returnFocus={fromMenu ? menuButton : undefined} busy={busy}>
      {dialog === 'rename' ? <form onSubmit={event => { event.preventDefault(); if (label.trim()) update.mutate({ label: label.trim() }) }}>
        <label className="field">Folder label<input value={label} onChange={event => setLabel(event.target.value)} disabled={busy} /></label>
        <Button type="submit" variant="primary" busy={busy} disabled={!label.trim()}>Save label</Button>
      </form> : <div className="form-actions"><Button variant={dialog === 'tidy' ? 'primary' : 'destructive'} busy={busy} onClick={() => dialog === 'tidy' ? tidy.mutate() : remove.mutate()}>{dialog === 'tidy' ? 'Tidy files' : 'Stop watching'}</Button></div>}
    </Modal>}
  </article>
}

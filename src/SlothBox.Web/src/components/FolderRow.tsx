import { useEffect, useRef, useState } from 'react'
import * as Menu from '@radix-ui/react-dropdown-menu'
import { skipToken, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MoreHorizontal } from 'lucide-react'
import { ApiError, request } from '../api/client'
import { requireSuccess } from '../api/file-actions'
import type { Schema } from '../api/types'
import { Button } from './Button'
import { Modal } from './Modal'
import { useToasts } from './toast-context'

export function FolderRow({ folder, nativePicker }: { folder: Schema['FolderDto']; nativePicker: boolean }) {
  const client = useQueryClient()
  const { notify } = useToasts()
  const [dialog, setDialog] = useState<'tidy' | 'rename' | 'remove' | 'locate' | null>(null)
  const [label, setLabel] = useState(folder.label)
  const menuButton = useRef<HTMLButtonElement>(null)
  const [fromMenu, setFromMenu] = useState(false)
  const progressKey = ['folder-progress', Number(folder.id)]
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
        {folder.missing ? <>
          <Button aria-label={`Locate ${folder.label}`} onClick={() => setDialog('locate')}>Locate</Button>
          <Button aria-label={`Remove ${folder.label}`} onClick={() => setDialog('remove')}>Remove</Button>
        </> : <>
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
    {dialog === 'locate' && <LocateFolder folder={folder} nativePicker={nativePicker} onClose={() => setDialog(null)} />}
    {dialog && dialog !== 'locate' && <Modal title={dialog === 'tidy' ? `Tidy ${folder.baselineCount} files?` : dialog === 'rename' ? 'Rename label' : `Stop watching ${folder.label}?`}
      description={dialog === 'tidy' ? 'You can undo this with Undo since on Today.' : dialog === 'rename' ? 'This changes the label in SlothBox.' : 'Already-filed files will stay where they are.'} onClose={() => { setDialog(null); setFromMenu(false) }} returnFocus={fromMenu ? menuButton : undefined} busy={busy}>
      {dialog === 'rename' ? <form onSubmit={event => { event.preventDefault(); if (label.trim()) update.mutate({ label: label.trim() }) }}>
        <label className="field">Folder label<input value={label} onChange={event => setLabel(event.target.value)} disabled={busy} /></label>
        <Button type="submit" variant="primary" busy={busy} disabled={!label.trim()}>Save label</Button>
      </form> : <div className="form-actions"><Button variant={dialog === 'tidy' ? 'primary' : 'destructive'} busy={busy} onClick={() => dialog === 'tidy' ? tidy.mutate() : remove.mutate()}>{dialog === 'tidy' ? 'Tidy files' : 'Stop watching'}</Button></div>}
    </Modal>}
  </article>
}

// Locate points a missing folder at its new place (spec 4.5); the API applies the folder rules to the new path.
function LocateFolder({ folder, nativePicker, onClose }: { folder: Schema['FolderDto']; nativePicker: boolean; onClose: () => void }) {
  const client = useQueryClient()
  const { notify } = useToasts()
  const [path, setPath] = useState('')
  const started = useRef(false)
  const locate = useMutation({
    mutationFn: (selected: string) => request('patch', '/api/folders/{id}', { path: { id: Number(folder.id) }, body: { path: selected.trim() } }),
    onSuccess: async result => { onClose(); await client.invalidateQueries({ queryKey: ['folders'] }); notify({ message: `Watching ${result.label}` }) },
    onError: error => {
      // 409: the folder is still at its path, so this row was out of date; the next sweep watches it again.
      if (!(error instanceof ApiError) || error.status !== 409) return
      onClose(); notify({ message: error.message, tone: 'error' })
      void client.invalidateQueries({ queryKey: ['folders'] })
    },
  })
  const pick = useMutation({ mutationFn: () => request('post', '/api/system/pick-folder'),
    onSuccess: value => { if (value.path) { setPath(value.path); locate.mutate(value.path) } else onClose() } })
  // Open the native picker once; Strict Mode may repeat effect setup.
  useEffect(() => {
    if (!nativePicker || started.current) return
    started.current = true
    pick.mutate()
  }, [nativePicker, pick])
  // A failed folder dialog falls back to a typed path (spec 4.5).
  const typed = !nativePicker || pick.isError
  const busy = pick.isPending || locate.isPending
  const error = locate.error ?? pick.error
  return <Modal title={`Locate ${folder.label}`} description="Choose where this folder is now." onClose={onClose} busy={busy}>
    {busy && <p role="status">{pick.isPending ? 'Choose a folder in the folder dialog…' : 'Locating folder…'}</p>}
    {error && <p className="query-error" role="alert">{error.message}</p>}
    {typed ? <form onSubmit={event => { event.preventDefault(); if (path.trim()) locate.mutate(path) }}>
      <label className="field">Folder path<input value={path} disabled={busy} onChange={event => { setPath(event.target.value); locate.reset() }} /></label>
      <div className="form-actions"><Button type="submit" variant="primary" disabled={!path.trim()} busy={locate.isPending}>Locate folder</Button></div>
    </form> : <>{path && <p><code>{path}</code></p>}
      {!busy && <Button onClick={() => { locate.reset(); pick.mutate() }}>Pick another folder</Button>}</>}
  </Modal>
}

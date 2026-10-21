import { useEffect, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { request } from '../../api/client'
import type { Schema } from '../../api/types'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'

export function AddFolder({ nativePicker, setup = false, onClose, onAdd }: {
  nativePicker: boolean; setup?: boolean; onClose: () => void
  onAdd: (folder: Schema['CreateFolderRequest']) => Promise<void>
}) {
  const [path, setPath] = useState('')
  const [label, setLabel] = useState('')
  const [mode, setMode] = useState<Schema['FolderMode']>('move')
  const [tidy, setTidy] = useState(false)
  const [check, setCheck] = useState<Schema['FolderCheckDto'] | null>(null)
  const started = useRef(false)
  const validate = useMutation({ mutationFn: async (selected: string) => {
    const result = await request('post', '/api/folders/check', { body: { path: selected.trim() } })
    return { path: selected.trim(), result }
  }, onSuccess: value => { setPath(value.path); setCheck(value.result); setMode(value.result.copyOnly ? 'copy' : 'move') } })
  const pick = useMutation({ mutationFn: () => request('post', '/api/system/pick-folder'),
    onSuccess: value => { if (value.path) { setPath(value.path); validate.mutate(value.path) } else onClose() } })
  // Open the native picker once; Strict Mode may repeat effect setup.
  useEffect(() => {
    if (!nativePicker || started.current) return
    started.current = true
    pick.mutate()
  }, [nativePicker, pick])
  const save = useMutation({ mutationFn: () => onAdd({ path, label: label.trim() || null, mode: check?.copyOnly ? 'copy' : mode, tidyExisting: setup ? false : tidy }), onSuccess: onClose })
  const busy = pick.isPending || validate.isPending || save.isPending
  const error = save.error || validate.error || pick.error
  return <Modal title="Add folder" description={setup ? 'Existing files will stay where they are.' : 'Choose a folder for SlothBox to watch.'} onClose={onClose} busy={busy}>
    {busy && !save.isPending && <p role="status">{pick.isPending ? 'Choose a folder in the folder dialog…' : 'Checking folder…'}</p>}
    {error && <p className="query-error" role="alert">{error.message}</p>}
    {nativePicker ? <>{path && <p><code>{path}</code></p>}
      {!check && !busy && <Button onClick={() => pick.mutate()}>Pick another folder</Button>}</> :
      <form onSubmit={event => { event.preventDefault(); setCheck(null); validate.mutate(path) }}>
        <label className="field">Folder path<input value={path} disabled={busy} onChange={event => { setPath(event.target.value); setCheck(null) }} /></label>
        {!check && <Button type="submit" disabled={!path.trim()} busy={busy}>Check folder</Button>}
      </form>}
    {check && !check.accepted && <div className="folder-refusal"><p role="alert">{check.reason || 'This folder cannot be watched.'}</p>
      <Button disabled={busy} onClick={() => { setCheck(null); if (nativePicker) pick.mutate() }}>Pick another folder</Button></div>}
    {check?.accepted && <form onSubmit={event => { event.preventDefault(); save.mutate() }}>
      <label className="field">Label<input value={label} onChange={event => setLabel(event.target.value)} disabled={busy} /></label>
      <div className="field"><label htmlFor="add-folder-mode">Mode</label><select id="add-folder-mode" value={mode} disabled={check.copyOnly || busy} onChange={event => setMode(event.target.value as Schema['FolderMode'])}>
        <option value="move">Move</option><option value="copy">Copy only</option>
      </select></div>
      {check.copyOnly && <p>{check.reason || 'Copy only (cloud folder)'}</p>}
      <p>{check.existingFileCount} files are already here</p>
      {setup ? <p>Files already in these folders stay where they are. You can tidy them later from Folders.</p> : <fieldset className="folder-existing" disabled={busy}>
        <legend className="sr-only">Existing files</legend>
        <label><input type="radio" name="existing" checked={!tidy} onChange={() => setTidy(false)} /> Leave them</label>
        <label><input type="radio" name="existing" checked={tidy} onChange={() => setTidy(true)} /> Tidy them now</label>
      </fieldset>}
      <div className="form-actions"><Button type="submit" variant="primary" busy={busy}>{setup ? 'Add to setup' : 'Watch folder'}</Button></div>
    </form>}
  </Modal>
}

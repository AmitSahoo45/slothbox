import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { request } from '../../api/client'
import { setupQuery } from '../../api/setup-query'
import { Button } from '../../components/Button'
import { FolderRow } from '../../components/FolderRow'
import { QueryError } from '../../components/QueryError'
import { AddFolder } from './AddFolder'
import { PhoneBlock } from './PhoneBlock'

export function Folders() {
  const setup = useQuery(setupQuery)
  const folders = useQuery({ queryKey: ['folders'], queryFn: ({ signal }) => request('get', '/api/folders', { signal }) })
  const client = useQueryClient()
  const [adding, setAdding] = useState(false)
  return <section className="content-page folders-page">
    <div className="page-toolbar"><div>{setup.data && <><p>Sorted files go to <code>{setup.data.outputRoot}</code></p><p className="field-hint">This can't be changed in this version.</p></>}</div>
      <Button variant="primary" disabled={!setup.data} onClick={() => setAdding(true)}>Add folder</Button></div>
    {setup.error && <QueryError error={setup.error} retry={() => { void setup.refetch() }} label="Retry folder settings" />}
    <h2>Watched folders</h2>
    {folders.isPending && <p role="status">Loading folders…</p>}
    {folders.error && <QueryError error={folders.error} retry={() => { void folders.refetch() }} label="Retry folders" />}
    <div className="folder-list">{folders.data?.map(folder => <FolderRow key={folder.id} folder={folder} />)}</div>
    {folders.data?.length === 0 && <p className="empty-state">No watched folders yet. Add a folder to get started.</p>}
    <PhoneBlock />
    {adding && setup.data && <AddFolder nativePicker={setup.data.nativePickerAvailable} onClose={() => setAdding(false)} onAdd={async body => {
      await request('post', '/api/folders', { body })
      await client.invalidateQueries({ queryKey: ['folders'] })
    }} />}
  </section>
}

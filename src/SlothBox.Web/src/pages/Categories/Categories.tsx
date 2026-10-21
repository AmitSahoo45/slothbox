import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { request } from '../../api/client'
import { requireSuccess } from '../../api/file-actions'
import type { Schema } from '../../api/types'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { QueryError } from '../../components/QueryError'
import { typeFolders } from '../../components/format'
import { useToasts } from '../../components/toast-context'

type Category = Schema['CategoryDto']
type CategoryType = Exclude<Schema['FileKind'], 'unknown'>
const types: CategoryType[] = ['receipt', 'screenshot', 'document', 'other']

export function Categories() {
  const query = useQuery({ queryKey: ['categories'], queryFn: ({ signal }) => request('get', '/api/categories', { signal }) })
  const [type, setType] = useState<CategoryType>('receipt')
  const [editing, setEditing] = useState<Category | 'new' | null>(null)
  const current = editing && editing !== 'new' ? query.data?.find(item => item.id === editing.id) : undefined
  return <section className="content-page categories-page">
    <div className="page-toolbar">
      <fieldset className="segmented category-types"><legend className="sr-only">Category type</legend>
        {types.map(value => <label key={value}><input type="radio" name="category-type" checked={value === type}
          onChange={() => setType(value)} /><span>{typeFolders[value]}</span></label>)}
      </fieldset>
      {type !== 'other' && <Button variant="primary" disabled={!query.data || !!query.error} onClick={() => setEditing('new')}>Add category</Button>}
    </div>
    {query.isPending && <p role="status">Loading categories…</p>}
    {query.error && <QueryError error={query.error} retry={() => { void query.refetch() }} label="Retry categories" />}
    {type === 'other' && <p className="field-hint">These built-in folders are sorted by extension.</p>}
    <div className="category-list">
      <div className="category-row category-head" aria-hidden="true"><span>Name</span><span>What Gemma reads</span><span>Files</span><span /></div>
      {query.data?.filter(item => item.type === type).map(category => <article className="category-row" key={category.id} aria-label={category.name}>
        <h2>{category.name}{category.isSystem ? ' (built in)' : ''}</h2>
        <p>{category.description}</p>
        <span className="category-count" aria-label={`${category.fileCount} files`}>{category.fileCount}<span className="narrow-only"> files</span></span>
        {!category.isSystem && type !== 'other' && <Button variant="quiet" aria-label={`Edit ${category.name}`} onClick={() => setEditing(category)}>Edit</Button>}
      </article>)}
    </div>
    {query.data && !query.data.some(item => item.type === type) && <p className="empty-state">No categories here yet.</p>}
    {editing && <CategoryEditor key={editing === 'new' ? 'new' : editing.id} initial={editing === 'new' ? undefined : editing}
      current={current} type={type} categories={query.data ?? []} unavailable={!!query.error || (editing !== 'new' && !current)} onClose={() => setEditing(null)} />}
  </section>
}

function CategoryEditor({ initial, current, type, categories, unavailable, onClose }: {
  initial?: Category; current?: Category; type: CategoryType; categories: Category[]; unavailable: boolean; onClose: () => void
}) {
  const [draft, setDraft] = useState<Partial<{ name: string; description: string; folder: string }>>({})
  const name = draft.name ?? current?.name ?? initial?.name ?? ''
  const description = draft.description ?? current?.description ?? initial?.description ?? ''
  const client = useQueryClient()
  const { notify } = useToasts()
  const locked = Number(current?.fileCount ?? 0) > 0
  const duplicate = categories.some(item => item.id !== initial?.id && item.type === type && item.name === name.trim())
  const folderValue = locked ? current!.folder : draft.folder ?? current?.folder ?? initial?.folder ?? name
  const valid = !unavailable && !current?.isSystem && type !== 'other' && name.trim() && folderValue.trim() && !duplicate
  const refresh = async () => {
    await Promise.all(['categories', 'files', 'activity', 'review', 'search', 'receipts'].map(key => client.invalidateQueries({ queryKey: [key] })))
  }
  const failure = (error: Error) => {
    notify({ message: error.message, tone: 'error' })
    void client.invalidateQueries({ queryKey: ['categories'] })
  }
  const save = useMutation({
    mutationFn: () => initial
      ? request('put', '/api/categories/{id}', { path: { id: Number(initial.id) }, body: { name: name.trim(), description: description.trim(), folder: locked ? null : folderValue.trim() } })
      : request('post', '/api/categories', { body: { type, name: name.trim(), description: description.trim(), folder: folderValue.trim() } }),
    onSuccess: async () => { await refresh(); onClose(); notify({ message: initial ? 'Category saved' : 'Category added' }) }, onError: failure,
  })
  const remove = useMutation({
    mutationFn: async () => requireSuccess(await request('delete', '/api/categories/{id}', { path: { id: Number(initial!.id) } })),
    onSuccess: async () => { await refresh(); onClose(); notify({ message: 'Category removed' }) }, onError: failure,
  })
  const busy = save.isPending || remove.isPending
  return <Modal title={initial ? 'Edit category' : 'Add category'} description={`Categories for ${typeFolders[type].toLowerCase()}.`} busy={busy} onClose={onClose}>
    <form onSubmit={event => { event.preventDefault(); if (valid && !busy) save.mutate() }}>
      <fieldset disabled={busy} className="plain-fields">
        <label className="field">Name<input value={name} required onChange={event => setDraft({ ...draft, name: event.target.value })} aria-invalid={duplicate || undefined} aria-describedby={duplicate ? 'category-name-error' : undefined} /></label>
        {duplicate && <p id="category-name-error" className="field-error" role="alert">A category with this name already exists in {typeFolders[type]}.</p>}
        <label className="field">Description<textarea value={description} rows={3} onChange={event => setDraft({ ...draft, description: event.target.value })} aria-describedby="category-description-hint" /></label>
        <p id="category-description-hint" className="field-hint">Gemma reads this to decide. Say what belongs here, with examples.</p>
        <label className="field category-folder">Folder name<input value={folderValue} required disabled={locked} onChange={event => setDraft({ ...draft, folder: event.target.value })} aria-describedby={locked ? 'category-folder-hint' : undefined} /></label>
        {locked && <p id="category-folder-hint" className="field-hint">The folder name is fixed once files are in it.</p>}
        {unavailable && <p role="alert" className="field-error">This category could not be checked. Close this dialog and refresh the list.</p>}
        <div className="form-actions">
          <Button type="submit" variant="primary" busy={save.isPending} disabled={!valid || busy}>{initial ? 'Save category' : 'Add category'}</Button>
          {initial && <span className="disabled-tip" tabIndex={locked ? 0 : undefined} aria-label={locked ? 'Category cannot be removed' : undefined} aria-describedby={locked ? 'category-remove-hint' : undefined}>
            <Button variant="destructive" busy={remove.isPending} disabled={locked || unavailable || current?.isSystem || busy} onClick={() => remove.mutate()}>Remove category</Button>
            {locked && <span role="tooltip" id="category-remove-hint">Move its files to another category first.</span>}
          </span>}
        </div>
      </fieldset>
    </form>
  </Modal>
}

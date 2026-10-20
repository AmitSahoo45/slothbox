import { useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { Schema } from '../../api/types'
import { request } from '../../api/client'
import { useFileActions } from '../../api/file-actions'
import { Button } from '../Button'
import { QueryError } from '../QueryError'
import { typeNames } from '../format'

export function ChangeFile({ details, onClose }: { details: Schema['FileDetailsDto']; onClose: () => void }) {
  const formId = useId()
  const [type, setType] = useState(details.file.type)
  const [categoryId, setCategoryId] = useState(String(details.categoryId ?? ''))
  const categories = useQuery({ queryKey: ['categories'],
    queryFn: ({ signal }) => request('get', '/api/categories', { signal }) })
  const { refile } = useFileActions()
  const choices = categories.data?.filter(category => category.type === type) ?? []
  const category = choices.find(item => String(item.id) === categoryId)
  return <form className="change-file" onSubmit={event => {
    event.preventDefault()
    if (!category || type === 'unknown' || refile.isPending) return
    refile.mutate({ id: details.file.fileId, body: { type, categoryId: category.id,
      title: details.file.title, receipt: type === 'receipt' ? details.receipt : null } }, { onSuccess: onClose })
  }}>
    <h3>Change filing</h3>
    {categories.error && <QueryError error={categories.error} retry={() => { void categories.refetch() }} label="Retry categories" />}
    <div className="field"><label htmlFor={`${formId}-type`}>Type</label><select id={`${formId}-type`} value={type} disabled={refile.isPending} onChange={event => {
      setType(event.target.value as Schema['FileKind']); setCategoryId('')
    }}>{Object.entries(typeNames).map(([value, label]) => <option key={value} value={value} disabled={value === 'unknown'}>{label}</option>)}</select></div>
    <div className="field"><label htmlFor={`${formId}-category`}>Category</label><select id={`${formId}-category`} value={categoryId} disabled={refile.isPending} onChange={event => setCategoryId(event.target.value)}>
      <option value="">Choose a category</option>
      {choices.map(category => <option key={category.id} value={category.id}>{category.name}</option>)}
    </select></div>
    <div className="form-actions">
      <Button type="submit" variant="primary" busy={refile.isPending} disabled={!category || type === 'unknown'}>Save changes</Button>
      <Button disabled={refile.isPending} onClick={onClose}>Cancel</Button>
    </div>
  </form>
}

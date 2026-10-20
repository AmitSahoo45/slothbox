import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { request } from '../../api/client'
import { useFileActions } from '../../api/file-actions'
import { Button } from '../../components/Button'
import { Thumbnail } from '../../components/Thumbnail'
import { QueryError } from '../../components/QueryError'
import { typeNames } from '../../components/format'

export function Search() {
  const [params, setParams] = useSearchParams()
  const [draft, setDraft] = useState(params.get('q') ?? '')
  const q = params.get('q') ?? ''
  const [previousQuery, setPreviousQuery] = useState(q)
  if (q !== previousQuery) { setPreviousQuery(q); setDraft(q) }
  const [type, setType] = useState('')
  const [categoryId, setCategory] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  useEffect(() => {
    if (draft.trim() === q) return
    const timer = setTimeout(() => setParams(previous => {
      const next = new URLSearchParams(previous)
      if (draft.trim()) next.set('q', draft.trim()); else next.delete('q')
      return next
    }, { replace: true }), 400)
    return () => clearTimeout(timer)
  }, [draft, q, setParams])
  const invalidDates = Boolean(from && to && from > to)
  const categories = useQuery({ queryKey: ['categories'], queryFn: ({ signal }) => request('get', '/api/categories', { signal }) })
  const result = useQuery({ queryKey: ['search', q, type, categoryId, from, to], enabled: Boolean(q) && !invalidDates,
    queryFn: ({ signal }) => request('get', '/api/search', { signal, query: {
      q, type: type || undefined, categoryId: categoryId || undefined, from: from || undefined, to: to || undefined,
    } }) })
  const { open } = useFileActions()
  return <section className="content-page search-page" aria-label="File search">
    <form className="search-form" onSubmit={event => {
      event.preventDefault()
      setParams(previous => { const next = new URLSearchParams(previous); next.set('q', draft.trim()); return next }, { replace: true })
      if (draft.trim() === q) void result.refetch()
    }}>
      <label className="field search-input"><span className="sr-only">Search your files</span>
        <input type="search" data-search-input placeholder="Search your files…" value={draft} onChange={event => setDraft(event.target.value)} /></label>
      <Button type="submit" variant="primary" disabled={!draft.trim() || invalidDates}>Search</Button>
    </form>
    <div className="search-filters">
      <div className="field"><label htmlFor="search-type">Type</label><select id="search-type" value={type} onChange={event => { setType(event.target.value); setCategory('') }}>
        <option value="">All</option>{Object.entries(typeNames).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
      </select></div>
      <div className="field"><label htmlFor="search-category">Category</label><select id="search-category" value={categoryId} onChange={event => setCategory(event.target.value)}>
        <option value="">All</option>{categories.data?.filter(category => !type || category.type === type).map(category => <option key={category.id} value={category.id}>{category.name}</option>)}
      </select></div>
      <label className="field">From date<input type="date" value={from} onChange={event => setFrom(event.target.value)} /></label>
      <label className="field">To date<input type="date" value={to} onChange={event => setTo(event.target.value)} /></label>
    </div>
    {categories.error && <QueryError error={categories.error} retry={() => { void categories.refetch() }} label="Retry categories" />}
    {invalidDates && <p role="alert">The end date must be on or after the start date.</p>}
    {!q ? <p className="empty-state">Try: jio bill, reddit thread about dotnet, electricity October</p> : <>
      {result.isFetching && <p role="status">Searching…</p>}
      {result.error && <QueryError error={result.error} retry={() => { void result.refetch() }} label="Retry search" />}
      {!invalidDates && result.data && <>
        <p className="result-count" role="status">{result.data.items.length} {result.data.items.length === 1 ? 'result' : 'results'}</p>
        {!result.data.items.length && <p className="empty-state">No files match. Try fewer words, or describe what the file was about.</p>}
        <div className="search-results">{result.data.items.map(hit => {
          const file = hit.file.file
          const next = new URLSearchParams(params); next.set('file', file.fileId)
          const match = hit.matchedWords && hit.matchedMeaning ? 'Matched words and meaning' : hit.matchedWords ? 'Matched words' : hit.matchedMeaning ? 'Matched meaning' : null
          return <article className="search-result" key={file.fileId} onClick={event => {
            if ((event.target as HTMLElement).closest('a, button')) return
            const link = event.currentTarget.querySelector('a'); link?.focus(); link?.click()
          }}>
            <Thumbnail file={file} /><div className="search-result-copy">
              <div className="result-heading"><h2><Link to={`?${next}`}>{file.title || file.newName}</Link></h2>{match && <span className="match-label">{match}</span>}</div>
              <p className="literal">{file.newName}</p><p><code>{file.folder}</code></p>
              {hit.file.caption && <p className="search-caption">{hit.file.caption}</p>}
              <div className="form-actions"><Button busy={open.isPending} onClick={() => open.mutate({ id: file.fileId, action: 'open' })}>Open</Button>
                <Button busy={open.isPending} onClick={() => open.mutate({ id: file.fileId, action: 'reveal' })}>Show in folder</Button></div>
            </div>
          </article>
        })}</div>
      </>}
    </>}
  </section>
}

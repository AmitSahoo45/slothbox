import { useEffect, useReducer } from 'react'
import { useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { request } from '../../api/client'
import type { Schema } from '../../api/types'

type Item = Schema['ActivityItemDto']
type Page = Schema['ActivityPageDto'] & { startedAt: number }
type State = {
  rows: Item[]; pending: Item[]; arriving: string[]; loaded: boolean; reviewNotice: string
  events: Record<string, { file: Item; receivedAt: number }>
}
type Action =
  | { kind: 'snapshot'; rows: Item[]; scrolled: boolean; startedAt: number }
  | { kind: 'event'; name: string; data: Item | { fileId: string }; scrolled: boolean; receivedAt: number }
  | { kind: 'reveal' }
const unique = (rows: Item[]) => Array.from(new Map(rows.map(row => [row.fileId, row])).values())
const newest = (rows: Item[]) => unique(rows).sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime())
const flatten = (data?: InfiniteData<Page>) => unique(data?.pages.flatMap(page => page.items) ?? [])

function reduce(state: State, action: Action): State {
  if (action.kind === 'reveal') return { ...state, rows: newest([...state.rows, ...state.pending]), pending: [],
    arriving: state.pending.filter(row => row.status === 'filed').map(row => row.fileId) }
  if (action.kind === 'snapshot') {
    const events = Object.fromEntries(Object.entries(state.events).filter(([, event]) => event.receivedAt > action.startedAt))
    const rows = unique([...action.rows, ...Object.values(events).map(event => event.file)])
    if (!state.loaded) return { ...state, events, rows: newest(rows.filter(row =>
      !state.pending.some(pending => pending.fileId === row.fileId))), loaded: true }
    const known = new Map(state.rows.map(row => [row.fileId, row]))
    const updates = new Map(rows.map(row => [row.fileId, row]))
    const additions = rows.filter(row => !known.has(row.fileId))
    const hold = action.scrolled || state.pending.length > 0
    const arrivals = hold ? additions.filter(row => new Date(row.at).getTime() > new Date(state.rows[0]?.at ?? 0).getTime()) : []
    const held = new Set([...state.pending, ...arrivals].map(row => row.fileId))
    const merged = unique([
      ...state.rows.map(row => updates.get(row.fileId) ?? row),
      ...additions.filter(row => !held.has(row.fileId)),
    ])
    return { ...state, events, rows: hold ? merged : newest(merged),
      pending: unique([...state.pending.map(row => updates.get(row.fileId) ?? row), ...arrivals]) }
  }
  const { name, data } = action
  if (!data?.fileId) return state
  // An undo may restore a previous filing or Needs review. Its GET response supplies that state.
  if (name === 'file.undone') return state
  if (!('at' in data)) return state
  const events = { ...state.events, [data.fileId]: { file: data, receivedAt: action.receivedAt } }
  const exists = state.rows.some(row => row.fileId === data.fileId)
  const pending = state.pending.some(row => row.fileId === data.fileId)
  const reviewNotice = name === 'file.review' ? `${data.title || data.newName} needs review.` : state.reviewNotice
  if (exists || pending) return { ...state, events, reviewNotice,
    rows: state.rows.map(row => row.fileId === data.fileId ? data : row),
    pending: state.pending.map(row => row.fileId === data.fileId ? data : row) }
  if (name === 'file.refiled') return { ...state, events }
  if (action.scrolled || state.pending.length) return { ...state, events, reviewNotice, pending: [data, ...state.pending] }
  return { ...state, events, reviewNotice, rows: newest([data, ...state.rows]),
    arriving: name === 'file.filed' ? [data.fileId, ...state.arriving] : state.arriving }
}

export function useActivity() {
  const client = useQueryClient()
  const query = useInfiniteQuery({
    queryKey: ['activity'], initialPageParam: undefined as string | undefined,
    queryFn: async ({ signal, pageParam }): Promise<Page> => {
      const startedAt = performance.now()
      const page = await request('get', '/api/activity', { signal, query: { limit: 50, cursor: pageParam } })
      return { ...page, startedAt }
    },
    getNextPageParam: page => page.nextCursor ?? undefined,
  })
  const [state, dispatch] = useReducer(reduce, {
    rows: flatten(query.data), pending: [], arriving: [], loaded: !!query.data, reviewNotice: '', events: {},
  })
  useEffect(() => {
    const unsubscribe = client.getQueryCache().subscribe(event => {
      if (event.type === 'updated' && event.action.type === 'success' && event.query.queryKey[0] === 'activity') {
        const data = event.query.state.data as InfiniteData<Page>
        dispatch({ kind: 'snapshot', rows: flatten(data), scrolled: window.scrollY > 80,
          startedAt: Math.min(...data.pages.map(page => page.startedAt)) })
      }
    })
    const onFile = (event: Event) => {
      const { name, data } = (event as CustomEvent<{ name: string; data: Item | { fileId: string } }>).detail
      dispatch({ kind: 'event', name, data, scrolled: window.scrollY > 80, receivedAt: performance.now() })
    }
    window.addEventListener('slothbox:file-event', onFile)
    return () => { unsubscribe(); window.removeEventListener('slothbox:file-event', onFile) }
  }, [client])
  return { ...state, query, reveal: () => { dispatch({ kind: 'reveal' }); window.scrollTo({ top: 0, behavior: 'instant' }) } }
}

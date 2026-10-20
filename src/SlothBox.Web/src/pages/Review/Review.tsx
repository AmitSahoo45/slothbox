import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { reviewQuery } from '../../api/queries'
import { request } from '../../api/client'
import { requireSuccess } from '../../api/file-actions'
import type { Schema } from '../../api/types'
import { QueryError } from '../../components/QueryError'
import { useToasts } from '../../components/toast-context'
import { ReviewCard } from './ReviewCard'
import { useState } from 'react'

export function Review() {
  const client = useQueryClient()
  const [activeId, setActiveId] = useState<string | null>(null)
  const [transitionId, setTransitionId] = useState<string | null>(null)
  const review = useQuery(reviewQuery)
  const categories = useQuery({ queryKey: ['categories'],
    queryFn: ({ signal }) => request('get', '/api/categories', { signal }) })
  const { notify } = useToasts()
  const skip = useMutation({
    mutationFn: async (id: string) => requireSuccess(await request('post', '/api/review/{id}/skip', { path: { id } })),
    onSuccess: async (_, id) => {
      await client.cancelQueries({ queryKey: ['review'] })
      const previous = client.getQueryData<Schema['ReviewItemDto'][]>(['review'])
      if (previous) {
        const reordered = [...previous.filter(item => item.file.file.fileId !== id), ...previous.filter(item => item.file.file.fileId === id)]
        client.setQueryData(['review'], reordered)
        setActiveId(reordered[0]?.file.file.fileId ?? null)
        setTransitionId(reordered[0]?.file.file.fileId ?? null)
      }
    },
    onError: error => notify({ tone: 'error', message: error.message }),
  })
  const items = review.data ?? []
  const active = items.find(item => item.file.file.fileId === activeId) ?? items[0]
  if (active && active.file.file.fileId !== activeId) setActiveId(active.file.file.fileId)
  return <section className="review-page" aria-label="Review queue">
    {review.isPending && <p>Loading files that need you…</p>}
    {review.error && <QueryError error={review.error} retry={() => { void review.refetch() }} label="Retry review" />}
    {categories.error && <QueryError error={categories.error} retry={() => { void categories.refetch() }} label="Retry categories" />}
    {review.data && !items.length && <div className="empty-state"><p>Nothing needs you. SlothBox handled everything.</p></div>}
    {active && <>
      <p className="review-position">1 of {items.length} left</p>
      <ReviewCard key={active.file.file.fileId} item={active} categories={categories.data ?? []}
        arriving={active.file.file.fileId === transitionId}
        skipping={skip.isPending} onSkip={() => skip.mutate(active.file.file.fileId)}
        onFiled={id => {
          const remaining = client.getQueryData<Schema['ReviewItemDto'][]>(['review'])?.filter(item => item.file.file.fileId !== id)
          if (remaining) {
            setTransitionId(remaining[0]?.file.file.fileId ?? null)
            client.setQueryData(['review'], remaining)
          }
        }} />
    </>}
  </section>
}

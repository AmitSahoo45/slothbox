import { useMutation, useQueryClient } from '@tanstack/react-query'
import { request } from './client'
import type { Schema } from './types'
import { useToasts } from '../components/toast-context'
import { typeFolders } from '../components/format'

export function requireSuccess<T extends { success: boolean; reason?: string | null; message?: string | null }>(result: T) {
  if (!result.success) throw new Error(result.reason || result.message || 'The action could not be completed. Try again.')
  return result
}
export function useFileActions() {
  const client = useQueryClient()
  const { notify } = useToasts()
  const refresh = () => {
    for (const key of ['activity', 'review', 'stats', 'files', 'receipts', 'search', 'categories', 'ignored']) {
      void client.invalidateQueries({ queryKey: [key] })
    }
  }
  const failure = (error: Error) => notify({ message: error.message, tone: 'error' })
  const undo = useMutation({
    mutationFn: async (id: string) => requireSuccess(await request('post', '/api/files/{id}/undo', { path: { id } })),
    onSuccess: () => { refresh(); notify({ message: 'Undone' }) }, onError: failure,
  })
  const refile = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schema['RefileRequest'] }) =>
      request('post', '/api/files/{id}/refile', { path: { id }, body }),
    onSuccess: async data => {
      await client.cancelQueries({ queryKey: ['files', data.file.fileId] })
      client.setQueryData(['files', data.file.fileId], data)
      // Keep the authoritative mutation response until a later event refreshes this file.
      for (const key of ['activity', 'review', 'stats', 'receipts', 'search', 'categories']) {
        void client.invalidateQueries({ queryKey: [key] })
      }
      notify({ message: `Filed to ${typeFolders[data.file.type]} / ${data.file.category}`,
        action: { label: 'Undo', altText: 'Undo filing this file', onClick: () => undo.mutate(data.file.fileId) } })
    },
    onError: failure,
  })
  const open = useMutation({
    mutationFn: async ({ id, action }: { id: string; action: 'open' | 'reveal' }) =>
      requireSuccess(await request('post', action === 'open' ? '/api/files/{id}/open' : '/api/files/{id}/reveal', { path: { id } })),
    onError: failure,
  })
  return { undo, refile, open, refresh }
}

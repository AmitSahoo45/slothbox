import { QueryClient, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { request } from './client'

export const createQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { staleTime: 15000, retry: false },
    mutations: { retry: false },
  },
})

export const statusQuery = queryOptions({
  queryKey: ['status'],
  queryFn: ({ signal }) => request('get', '/api/status', { signal }),
  refetchInterval: 30000,
})
export const reviewQuery = queryOptions({
  queryKey: ['review'],
  queryFn: ({ signal }) => request('get', '/api/review', { signal }),
})

export function useQueueControl() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (action: 'pause' | 'resume') => request('post', `/api/${action}`),
    onSuccess: async data => {
      await client.cancelQueries({ queryKey: statusQuery.queryKey })
      client.setQueryData(statusQuery.queryKey, data)
    },
  })
}

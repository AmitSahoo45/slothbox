import { request } from './client'

export const setupQuery = { queryKey: ['setup'], queryFn: ({ signal }: { signal: AbortSignal }) => request('get', '/api/setup', { signal }) }

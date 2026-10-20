import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { EventPayloads, Schema } from './types'
import { statusQuery } from './queries'

const fileEvents = ['file.filed', 'file.review', 'file.duplicate', 'file.undone', 'file.refiled'] as const

export function useServerEvents() {
  const client = useQueryClient()
  const [disconnected, setDisconnected] = useState(false)
  useEffect(() => {
    let pendingQueue: Schema['QueueChangedEvent'] | undefined
    const unsubscribe = client.getQueryCache().subscribe(event => {
      if (event.type !== 'updated' || event.action.type !== 'success' ||
        event.query.queryKey[0] !== 'status' || !pendingQueue) return
      const queue = pendingQueue
      pendingQueue = undefined
      client.setQueryData<Schema['StatusDto']>(statusQuery.queryKey, previous =>
        previous ? { ...previous, queue } : previous)
    })
    const source = new EventSource('/events')
    const listen = <K extends keyof EventPayloads>(name: K, update: (data: EventPayloads[K]) => void) => {
      source.addEventListener(name, event => {
        try { update(JSON.parse((event as MessageEvent<string>).data)) }
        catch { /* A malformed event must not interrupt subsequent updates. */ }
      })
    }
    source.addEventListener('open', () => {
      setDisconnected(false)
      // Reconcile changes missed while disconnected.
      void client.invalidateQueries()
    })
    source.addEventListener('error', () => setDisconnected(true))
    listen('status.changed', data => {
      if (!data?.gemma || !data.database || !data.queue) return
      pendingQueue = undefined
      void client.cancelQueries({ queryKey: statusQuery.queryKey })
      client.setQueryData(statusQuery.queryKey, data)
    })
    listen('queue.changed', queue => {
      if (!queue || !Number.isFinite(Number(queue.waiting)) || !Number.isFinite(Number(queue.processing))) return
      if (!client.getQueryData(statusQuery.queryKey)) {
        pendingQueue = queue
        return
      }
      void client.cancelQueries({ queryKey: statusQuery.queryKey })
      client.setQueryData<Schema['StatusDto']>(statusQuery.queryKey, previous =>
        previous ? { ...previous, queue } : previous)
    })
    for (const name of fileEvents) {
      listen(name, () => {
        for (const key of ['activity', 'review', 'stats', 'files', 'receipts', 'search']) {
          void client.invalidateQueries({ queryKey: [key] })
        }
      })
    }
    listen('folder.tidy-progress', data => {
      client.setQueryData(['folder-progress', data.folderId], data)
      if (Number(data.done) >= Number(data.total)) void client.invalidateQueries({ queryKey: ['folders'] })
    })
    return () => { unsubscribe(); source.close() }
  }, [client])
  return { disconnected }
}

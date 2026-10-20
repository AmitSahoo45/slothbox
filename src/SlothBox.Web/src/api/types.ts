import type { components, operations } from './generated'

export type Schema = components['schemas']
export type JsonResponse<K extends keyof operations> =
  operations[K] extends { responses: { 200: { content: { 'application/json': infer T } } } } ? T : never
export type JsonOperation = {
  [K in keyof operations]: JsonResponse<K> extends never ? never : K
}[keyof operations]

export interface EventPayloads {
  'file.filed': Schema['FileFiledEvent']
  'file.review': Schema['FileReviewEvent']
  'file.duplicate': Schema['FileDuplicateEvent']
  'file.undone': Schema['FileUndoneEvent']
  'file.refiled': Schema['FileRefiledEvent']
  'queue.changed': Schema['QueueChangedEvent']
  'status.changed': Schema['StatusChangedEvent']
  'folder.tidy-progress': Schema['FolderTidyProgressEvent']
}

export type MockEvent = {
  [K in keyof EventPayloads]: { event: K; data: EventPayloads[K] }
}[keyof EventPayloads]

import type { paths } from './generated'
import type { Schema } from './types'

type Method = 'get' | 'post' | 'put' | 'patch' | 'delete'
type JsonResult<Operation> = Operation extends {
  responses: { 200: { content: { 'application/json': infer Result } } }
} ? Result : never
type JsonPath<M extends Method> = {
  [P in keyof paths]: JsonResult<paths[P][M]> extends never ? never : P
}[keyof paths]
type JsonBody<Operation> = Operation extends {
  requestBody: { content: { 'application/json': infer Body } }
} ? Body : never
type ParametersFor<Operation, Kind extends 'path' | 'query'> =
  Operation extends { parameters: infer Parameters }
    ? Kind extends keyof Parameters ? Parameters[Kind] : never : never

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

export async function request<M extends Method, P extends JsonPath<M>>(
  method: M, path: P,
  options: {
    signal?: AbortSignal; body?: JsonBody<paths[P][M]>
    path?: ParametersFor<paths[P][M], 'path'>
    query?: ParametersFor<paths[P][M], 'query'>
  } = {},
): Promise<JsonResult<paths[P][M]>> {
  const headers = new Headers({ Accept: 'application/json' })
  if (method !== 'get') headers.set('X-SlothBox', '1')
  if (options.body !== undefined) headers.set('Content-Type', 'application/json')
  let resolvedPath: string = path
  for (const [name, value] of Object.entries(options.path ?? {})) {
    resolvedPath = resolvedPath.replace(`{${name}}`, encodeURIComponent(String(value)))
  }
  if (resolvedPath.includes('{')) throw new Error('A required URL parameter is missing.')
  const url = new URL(resolvedPath, window.location.origin)
  for (const [name, value] of Object.entries(options.query ?? {})) {
    if (value !== undefined && value !== null) url.searchParams.set(name, String(value))
  }
  const response = await fetch(url, {
    method: method.toUpperCase(), headers, signal: options.signal,
    credentials: 'same-origin',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  })
  if (!response.ok) {
    let message = `Request failed (${response.status}). Try again.`
    try {
      const problem: Schema['ProblemDetails'] = await response.json()
      message = problem.detail || problem.title || message
    } catch { /* Keep the fallback for non-JSON responses. */ }
    throw new ApiError(response.status, message)
  }
  return response.json()
}

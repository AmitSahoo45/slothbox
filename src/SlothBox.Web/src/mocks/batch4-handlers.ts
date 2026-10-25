import { http, HttpResponse } from 'msw'
import type { Schema } from '../api/types'

export type SettingsState = { settings: Schema['SettingsDto']; ignored: Schema['IgnoredFileDto'][] }
const problem = (message: string, status = 400) => HttpResponse.json({ title: message, status }, { status })
const protection = (request: Request) => request.headers.get('X-SlothBox') === '1' ? null : problem('Missing X-SlothBox header', 403)

export function createCategoryHandlers(categories: Schema['CategoryDto'][], count: (id: number | string) => number, rename: (category: Schema['CategoryDto']) => void) {
  let nextId = Math.max(...categories.map(item => Number(item.id))) + 1
  return [
    http.get('*/api/categories', () => HttpResponse.json(categories.map(category => ({ ...category, fileCount: count(category.id) })))),
    http.post('*/api/categories', async ({ request }) => {
      const denied = protection(request)
      if (denied) return denied
      const body = await request.json() as Schema['CreateCategoryRequest']
      if (!['receipt', 'screenshot', 'document'].includes(body.type) || !body.name?.trim() || !body.folder?.trim()) return problem('Choose a name and folder for this category.')
      if (categories.some(item => item.type === body.type && item.name === body.name.trim())) return problem('A category with this name already exists.', 409)
      const category: Schema['CategoryDto'] = { ...body, name: body.name.trim(), folder: body.folder.trim(), id: nextId++, isSystem: false, fileCount: 0 }
      categories.push(category)
      return HttpResponse.json(category)
    }),
    http.put('*/api/categories/:id', async ({ request, params }) => {
      const denied = protection(request)
      if (denied) return denied
      const category = categories.find(item => String(item.id) === params.id)
      if (!category) return problem('Category not found', 404)
      if (category.isSystem || category.type === 'other') return problem('Built-in categories are read-only.', 409)
      const body = await request.json() as Schema['UpdateCategoryRequest']
      if (!body.name?.trim() || (body.folder !== null && !body.folder?.trim())) return problem('Choose a name and folder for this category.')
      if (categories.some(item => item.id !== category.id && item.type === category.type && item.name === body.name.trim())) return problem('A category with this name already exists.', 409)
      if (count(category.id) > 0 && body.folder !== null && body.folder !== category.folder) return problem('The folder name is fixed once files are in it.', 409)
      Object.assign(category, { name: body.name.trim(), description: body.description, folder: body.folder?.trim() ?? category.folder })
      rename(category)
      return HttpResponse.json({ ...category, fileCount: count(category.id) })
    }),
    http.delete('*/api/categories/:id', ({ request, params }) => {
      const denied = protection(request)
      if (denied) return denied
      const index = categories.findIndex(item => String(item.id) === params.id)
      if (index < 0) return problem('Category not found', 404)
      const category = categories[index]
      if (category.isSystem || count(category.id) > 0) return problem('Only empty custom categories can be removed.', 409)
      categories.splice(index, 1)
      return HttpResponse.json({ success: true, message: null })
    }),
  ]
}

export function createSettingsHandlers(state: SettingsState) {
  return [
    http.get('*/api/settings', () => HttpResponse.json(state.settings)),
    http.put('*/api/settings', async ({ request }) => {
      const denied = protection(request)
      if (denied) return denied
      const body = await request.json() as Schema['UpdateSettingsRequest']
      const seconds = ['receiptSeconds', 'screenshotSeconds', 'documentSeconds', 'otherSeconds'] as const
      if (typeof body.startAtLogin !== 'boolean' || !body.laziness || seconds.some(key => {
        const value = Number(body.laziness[key])
        return !Number.isInteger(value) || value < 0 || value > 2147483647
      })) return problem('Use whole seconds from 0 up.')
      let address: URL | null = null
      try { address = new URL(body.ollamaUrl.trim()) } catch { /* Refused below. */ }
      if (!address || !['http:', 'https:'].includes(address.protocol) || address.username || address.password || /[?#]/.test(body.ollamaUrl)) {
        return problem('Use an http or https address, like http://127.0.0.1:11436.')
      }
      state.settings = body
      return HttpResponse.json(state.settings)
    }),
    http.get('*/api/ignored', () => HttpResponse.json(state.ignored)),
    http.delete('*/api/ignored/:sha256', ({ request, params }) => {
      const denied = protection(request)
      if (denied) return denied
      const index = state.ignored.findIndex(file => file.sha256 === params.sha256)
      if (index < 0) return problem('This file is not on the ignored list.', 404)
      state.ignored.splice(index, 1)
      return HttpResponse.json({ success: true, message: null })
    }),
    http.post('*/api/search-data/rebuild', ({ request }) => protection(request) ?? HttpResponse.json({ queued: 3 })),
  ]
}

import { http, HttpResponse } from 'msw'
import type { MockEvent, Schema } from '../api/types'
import { responses, laziness } from './fixtures'
import { localDate } from '../components/format'
import { createCategoryHandlers, type SettingsState } from './batch4-handlers'

// Session-only synthetic files. No filesystem operations run in these handlers.
export function createBatch2Handlers(publish: (event: MockEvent) => void, settingsState?: SettingsState) {
  const categories: Schema['CategoryDto'][] = [
    ['receipt', 'Food and drink'], ['receipt', 'Shopping'], ['receipt', 'Bills and recharges'],
    ['screenshot', 'Conversations'], ['screenshot', 'Reference'], ['document', 'Learning'],
    ['document', 'Work'], ['other', 'Misc'],
    ['receipt', 'Other'], ['screenshot', 'Other'], ['document', 'Other'],
    ['other', 'Installers'], ['other', 'Archives'], ['other', 'Media'],
  ].map(([type, name], index) => ({
    ...responses.GetCategories[0], id: index + 1, type: type as Schema['FileKind'], name, folder: name,
    isSystem: type === 'other' || name === 'Other',
    description: name === 'Other' ? 'Anything that fits no other category' : type === 'other' ? 'Sorted by file extension' : `${name} files and related purchases`,
  }))
  const files = new Map<string, Schema['FileDetailsDto']>()
  const before = new Map<string, Schema['FileDetailsDto'][]>()
  let reviewOrder: string[] = []
  for (let index = 0; index < 56; index++) {
    const id = `10000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`
    const details: Schema['FileDetailsDto'] = structuredClone(responses.GetFile)
    const category = categories[index % categories.length]
    const at = new Date(Date.now() - index * 3600000).toISOString()
    details.file = { ...details.file, fileId: id, at, type: category.type, category: category.name,
      title: index === 0 ? 'Sample lunch' : `Sample ${category.type} ${index + 1}`,
      newName: index === 0 ? responses.GetFile.file.newName : `sample-${index + 1}.${category.type === 'document' ? 'pdf' : 'jpg'}`,
      thumbUrl: category.type === 'document' || category.type === 'other' ? null : `/api/files/${id}/thumb`,
      origin: index % 2 === 0 ? 'phone' : 'folder', folderLabel: index % 2 === 0 ? null : 'Sample input',
    }
    details.categoryId = category.id
    details.originalName = `sample-input-${index + 1}.jpg`
    details.originalPath = `D:\\SlothBox-Sample\\Incoming\\${details.originalName}`
    details.currentPath = `D:\\SlothBox-Sample\\Filed\\${details.file.newName}`
    details.receipt = category.type === 'receipt' ? { ...responses.GetFile.receipt, date: localDate(new Date(at)) } : null
    details.history = [{ ...details.history[0], id: index + 1, at, fromPath: details.originalPath, toPath: details.currentPath }]
    if (index >= 53) {
      details.file = { ...details.file, type: 'receipt', category: categories[0].name, status: 'review', canUndo: false }
      details.categoryId = 1
      details.receipt = { ...responses.GetFile.receipt, date: localDate(new Date(at)) }
      details.reviewReason = index === 53 ? 'missing_amount' : index === 54 ? 'missing_date' : 'unknown_type'
      if (index === 53) details.receipt.amount = null
      if (index === 54) details.receipt.date = null
      if (index === 55) details.file.type = 'unknown'
      reviewOrder.push(id)
    }
    files.set(id, details)
  }
  const missing = () => HttpResponse.json({ title: 'File not found', status: 404 }, { status: 404 })
  const protection = (request: Request) => request.headers.get('X-SlothBox') === '1' ? null
    : HttpResponse.json({ title: 'Missing X-SlothBox header', status: 403 }, { status: 403 })
  const ordered = () => [...files.values()].map(item => item.file).sort((a, b) => b.at.localeCompare(a.at))
  const undoableSince = (since: Date) => ordered().filter(file => file.canUndo && new Date(file.at) >= since)
  const ledger = (month: string): Schema['ReceiptsDto'] => {
    const items = [...files.values()].filter(file => file.file.status === 'filed' && file.file.type === 'receipt' && file.receipt?.date?.startsWith(month))
      .map(file => ({ ...file.receipt!, fileId: file.file.fileId, categoryId: file.categoryId, category: file.file.category }))
    const totals = new Map<string, { currency: string; amount: number; count: number }>()
    const groups = new Map<string, Schema['CategoryTotalDto']>()
    for (const item of items) {
      if (item.amount == null || !item.currency) continue
      const total = totals.get(item.currency) ?? { currency: item.currency, amount: 0, count: 0 }
      total.amount += Number(item.amount); total.count++; totals.set(item.currency, total)
      if (item.categoryId == null) continue
      const key = `${item.currency}/${item.categoryId}`
      const group = groups.get(key) ?? { categoryId: item.categoryId, category: item.category, currency: item.currency, amount: 0 }
      group.amount = Number(group.amount) + Number(item.amount); groups.set(key, group)
    }
    return { month, items, totals: [...totals.values()], categories: [...groups.values()] }
  }
  const undo = (id: string): Schema['UndoResultDto'] => {
    const details = files.get(id)
    if (!details?.file.canUndo) return { fileId: id, success: false, restoredPath: null, reason: 'This file has no action to undo.' }
    const previous = before.get(id)?.pop()
    if (previous) {
      files.set(id, previous)
      if (previous.reviewReason && !reviewOrder.includes(id)) reviewOrder.push(id)
    } else {
      details.file = { ...details.file, status: 'undone', canUndo: false }
      settingsState?.ignored.push({ sha256: id.replaceAll('-', '').padEnd(64, '0'), fileName: details.originalName, at: new Date().toISOString(), reason: 'undone' })
    }
    publish({ event: 'file.undone', data: { fileId: id } })
    return { fileId: id, success: true, restoredPath: details.originalPath, reason: null }
  }
  return [
    http.get('*/api/search', ({ request }) => {
      const params = new URL(request.url).searchParams
      const q = params.get('q')?.trim().toLowerCase()
      const selected = q ? [...files.values()].filter(file => {
        const date = localDate(new Date(file.file.at))
        return file.file.status !== 'undone' && `${file.file.title} ${file.file.newName} ${file.caption}`.toLowerCase().includes(q)
          && (!params.get('type') || file.file.type === params.get('type'))
          && (!params.get('categoryId') || String(file.categoryId) === params.get('categoryId'))
          && (!params.get('from') || date >= params.get('from')!) && (!params.get('to') || date <= params.get('to')!)
      }) : []
      return HttpResponse.json({ items: selected.map((file, index) => ({ file, score: 1 / (index + 1), matchedWords: index % 3 !== 1, matchedMeaning: index % 3 !== 0 })) })
    }),
    http.get('*/api/receipts', ({ request }) => HttpResponse.json(ledger(new URL(request.url).searchParams.get('month') || localDate(new Date()).slice(0, 7)))),
    http.get('*/api/receipts/export.csv', ({ request }) => {
      const month = new URL(request.url).searchParams.get('month') || localDate(new Date()).slice(0, 7)
      const quote = (value: unknown) => `"${String(value ?? '').replaceAll('"', '""')}"`
      const rows = ledger(month).items.map(item => [item.date, item.vendor, item.amount, item.currency, item.category, item.paymentMethod].map(quote).join(','))
      return HttpResponse.text(['date,vendor,amount,currency,category,paid_by', ...rows].join('\r\n'), { headers: { 'Content-Type': 'text/csv', 'Content-Disposition': 'attachment; filename="receipts.csv"' } })
    }),
    ...createCategoryHandlers(categories, id => [...files.values()].filter(file => file.categoryId === id && file.file.status !== 'undone').length, category => {
      for (const file of [...files.values(), ...[...before.values()].flat()]) {
        if (file.categoryId === category.id) file.file = { ...file.file, category: category.name }
      }
    }),
    http.get('*/api/activity', ({ request }) => {
      const url = new URL(request.url)
      const offset = Math.max(0, Number(url.searchParams.get('cursor')) || 0)
      const limit = Math.max(1, Math.min(50, Number(url.searchParams.get('limit')) || 50))
      const items = ordered()
      return HttpResponse.json({ items: items.slice(offset, offset + limit),
        nextCursor: offset + limit < items.length ? String(offset + limit) : null })
    }),
    http.get('*/api/stats/laziness', ({ request }) => {
      const range = new URL(request.url).searchParams.get('range')
      const secondsPerType = settingsState?.settings.laziness ?? laziness
      const today = new Date()
      const monday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (today.getDay() + 6) % 7)
      const since = range === 'today' ? localDate(today) : range === 'all' ? '' : localDate(monday)
      const filed = ordered().filter(file => file.status === 'filed')
      const selected = filed.filter(file => localDate(new Date(file.at)) >= since)
      const receipts = selected.filter(file => file.type === 'receipt').flatMap(file => {
        const receipt = files.get(file.fileId)?.receipt
        return receipt ? [receipt] : []
      })
      const totals = new Map<string, { currency: string; amount: number; count: number }>()
      for (const receipt of receipts) {
        const currency = receipt.currency ?? 'INR'
        const total = totals.get(currency) ?? { currency, amount: 0, count: 0 }
        total.amount += Number(receipt.amount)
        total.count++
        totals.set(currency, total)
      }
      return HttpResponse.json({
        filesFiled: selected.length,
        minutesSaved: selected.reduce((sum, file) => sum + Number(secondsPerType[file.type === 'receipt' ? 'receiptSeconds' : file.type === 'screenshot' ? 'screenshotSeconds' : file.type === 'document' ? 'documentSeconds' : 'otherSeconds']), 0) / 60,
        receiptsLogged: receipts.length, receiptTotals: [...totals.values()], duplicatesCaught: 0, secondsPerType,
        days: Array.from({ length: 7 }, (_, index) => {
          const date = localDate(new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + index))
          return { date, count: filed.filter(file => localDate(new Date(file.at)) === date).length }
        }),
      } satisfies Schema['LazinessStatsDto'])
    }),
    http.get('*/api/review', () => HttpResponse.json(reviewOrder.map(id => ({
      file: files.get(id)!, reason: files.get(id)!.reviewReason,
    })))),
    http.get('*/api/files/:id', ({ params }) => {
      const file = files.get(String(params.id))
      return file ? HttpResponse.json(file) : missing()
    }),
    http.post('*/api/review/:id/skip', ({ params, request }) => {
      const denied = protection(request)
      if (denied) return denied
      const id = String(params.id)
      if (!reviewOrder.includes(id)) return missing()
      reviewOrder = [...reviewOrder.filter(item => item !== id), id]
      return HttpResponse.json({ success: true, message: null })
    }),
    http.post('*/api/files/:id/refile', async ({ params, request }) => {
      const denied = protection(request)
      if (denied) return denied
      const id = String(params.id)
      const details = files.get(id)
      if (!details) return missing()
      const body = await request.json() as Schema['RefileRequest']
      const category = categories.find(item => String(item.id) === String(body.categoryId) && item.type === body.type)
      if (!category) return HttpResponse.json({ title: 'Choose a category for this type.' }, { status: 400 })
      if (body.type === 'receipt' && (!body.receipt?.date || body.receipt.amount == null)) {
        return HttpResponse.json({ title: 'A receipt needs a date and amount before it can be filed.' }, { status: 400 })
      }
      before.set(id, [...before.get(id) ?? [], structuredClone(details)])
      const wasReview = !!details.reviewReason
      const updated: Schema['FileDetailsDto'] = {
        ...details, categoryId: category.id, reviewReason: null, receipt: body.receipt ?? null,
        file: { ...details.file, type: body.type, category: category.name, title: body.title ?? details.file.title,
          status: 'filed', canUndo: true, at: new Date().toISOString() },
      }
      files.set(id, updated)
      reviewOrder = reviewOrder.filter(item => item !== id)
      publish({ event: wasReview ? 'file.filed' : 'file.refiled', data: updated.file })
      return HttpResponse.json(updated)
    }),
    http.post('*/api/files/:id/undo', ({ params, request }) => protection(request) ?? HttpResponse.json(undo(String(params.id)))),
    ...(['open', 'reveal'] as const).map(action => http.post(`*/api/files/:id/${action}`, ({ params, request }) =>
      protection(request) ?? (files.has(String(params.id)) ? HttpResponse.json({ success: true, message: null }) : missing()))),
    http.post('*/api/undo/since', async ({ request }) => {
      const denied = protection(request)
      if (denied) return denied
      const { since } = await request.json() as Schema['UndoSinceRequest']
      const results = undoableSince(new Date(since)).map(file => undo(file.fileId))
      return HttpResponse.json({ undone: results.filter(item => item.success).length, results })
    }),
    http.get('*/api/undo/since', ({ request }) => {
      const since = new Date(new URL(request.url).searchParams.get('since') ?? '')
      if (!Number.isFinite(since.getTime())) return HttpResponse.json({ title: 'Choose the time to undo from.', status: 400 }, { status: 400 })
      return HttpResponse.json({ actions: undoableSince(since).length })
    }),
  ]
}

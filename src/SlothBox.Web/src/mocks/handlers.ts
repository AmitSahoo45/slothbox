import { http, HttpResponse } from 'msw'
import { events, responses } from './fixtures'
import { sampleJpeg } from './image'
import { sseResponse } from './sse'

const json = (operation: keyof typeof responses) => () => HttpResponse.json(responses[operation])
const image = () => new HttpResponse(sampleJpeg(), { headers: { 'Content-Type': 'image/jpeg' } })

// Match either local dashboard host or the Vite proxy; route coverage stays explicit.
export const handlers = [
  http.get('*/api/status', json('GetStatus')),
  http.post('*/api/pause', json('Pause')),
  http.post('*/api/resume', json('Resume')),
  http.get('*/api/activity', json('GetActivity')),
  http.get('*/api/stats/laziness', json('GetLazinessStats')),
  http.get('*/api/files/:id', json('GetFile')),
  http.get('*/api/files/:id/thumb', image),
  http.get('*/api/files/:id/preview', image),
  http.post('*/api/files/:id/open', json('OpenFile')),
  http.post('*/api/files/:id/reveal', json('RevealFile')),
  http.post('*/api/files/:id/undo', json('UndoFile')),
  http.post('*/api/files/:id/refile', json('Refile')),
  http.post('*/api/undo/last', json('UndoLast')),
  http.post('*/api/undo/since', json('UndoSince')),
  http.get('*/api/undo/since', json('PreviewUndoSince')),
  http.get('*/api/review', json('GetReview')),
  http.post('*/api/review/:id/skip', json('SkipReview')),
  http.get('*/api/search', json('Search')),
  http.get('*/api/receipts', json('GetReceipts')),
  http.get('*/api/receipts/export.csv', () => HttpResponse.text(
    'date,vendor,amount,currency,category\n2026-10-20,Sample Cafe,120,INR,Food and drink\n',
    { headers: { 'Content-Type': 'text/csv', 'Content-Disposition': 'attachment; filename="receipts.csv"' } })),
  http.get('*/api/categories', json('GetCategories')),
  http.post('*/api/categories', json('CreateCategory')),
  http.put('*/api/categories/:id', json('UpdateCategory')),
  http.delete('*/api/categories/:id', json('DeleteCategory')),
  http.get('*/api/folders', json('GetFolders')),
  http.post('*/api/folders/check', json('CheckFolder')),
  http.post('*/api/folders', json('CreateFolder')),
  http.patch('*/api/folders/:id', json('UpdateFolder')),
  http.post('*/api/folders/:id/tidy-existing', json('TidyExisting')),
  http.delete('*/api/folders/:id', json('DeleteFolder')),
  http.post('*/api/system/pick-folder', json('PickFolder')),
  http.get('*/api/settings', json('GetSettings')),
  http.put('*/api/settings', json('UpdateSettings')),
  http.get('*/api/setup', json('GetSetup')),
  http.post('*/api/setup/complete', json('CompleteSetup')),
  http.get('*/api/phone', json('GetPhone')),
  http.post('*/api/phone/rotate', json('RotatePhone')),
  http.get('*/api/ignored', json('GetIgnored')),
  http.delete('*/api/ignored/:sha256', json('AllowIgnored')),
  http.post('*/api/search-data/rebuild', json('RebuildSearchData')),
  http.get('*/events', () => sseResponse(events)),
  http.get('*/u/:token', () => HttpResponse.html(
    '<!doctype html><html lang="en"><title>SlothBox sample upload</title><body>Sample phone page</body></html>')),
  http.post('*/u/:token/upload', json('UploadPhoneFile')),
]

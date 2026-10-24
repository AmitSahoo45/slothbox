import { http, HttpResponse } from 'msw'
import type { MockEvent, Schema } from '../api/types'
import { responses } from './fixtures'

// Synthetic paths and session state only; no native dialogs or filesystem access.
export function createBatch3Handlers(getStatus: () => Schema['StatusDto'], setStatus: (status: Schema['StatusDto']) => void, publish: (event: MockEvent) => void) {
  let outputRoot = responses.GetSetup.outputRoot
  let folders: Schema['FolderDto'][] = [structuredClone(responses.GetFolders[0]),
    { ...responses.GetFolders[0], id: 2, label: 'Sample cloud folder', path: 'D:\\SlothBox-Sample\\Cloud', isCloud: true, baselineCount: 0 },
    { ...responses.GetFolders[0], id: 3, label: 'Sample missing folder', path: 'D:\\SlothBox-Sample\\Missing', missing: true, enabled: false, baselineCount: 0 }]
  let nextId = 4
  let phoneVersion = 0
  let phone: Schema['PhoneDto'] = structuredClone(responses.GetPhone)
  const setup = (): Schema['SetupDto'] => ({ complete: getStatus().setupComplete, outputRoot, status: getStatus(), nativePickerAvailable: false,
    suggestedFolders: folders.filter(folder => !folder.missing).map(({ path, label, mode }) => ({ path, label, mode })) })
  const denied = (request: Request) => request.headers.get('X-SlothBox') === '1' ? null : HttpResponse.json({ title: 'Missing X-SlothBox header' }, { status: 403 })
  const missing = () => HttpResponse.json({ title: 'Folder not found' }, { status: 404 })
  const check = (path: string): Schema['FolderCheckDto'] => {
    const absolute = /^[a-z]:[\\/].+/i.test(path) && !/^[a-z]:[\\/]*$/i.test(path)
    const copyOnly = /cloud/i.test(path)
    return { accepted: absolute, reason: !absolute ? 'A drive root or relative path cannot be watched.' : copyOnly ? 'Copy only (cloud folder)' : null, copyOnly, existingFileCount: 12 }
  }
  const tidy = (folder: Schema['FolderDto']) => {
    const total = Number(folder.baselineCount)
    let done = 0
    const timer = setInterval(() => {
      done = Math.min(total, done + Math.max(1, Math.ceil(total / 3)))
      if (done >= total) { clearInterval(timer); folder.baselineCount = 0; folder.filedCount = Number(folder.filedCount) + total }
      publish({ event: 'folder.tidy-progress', data: { folderId: folder.id, done, total } })
    }, 600)
    return total
  }
  return [
    http.get('*/api/setup', () => HttpResponse.json(setup())),
    http.post('*/api/setup/complete', async ({ request }) => {
      const error = denied(request); if (error) return error
      const body = await request.json() as Schema['CompleteSetupRequest']
      if (!check(body.outputRoot).accepted) return HttpResponse.json({ title: 'Choose an absolute output folder below a drive root.' }, { status: 400 })
      outputRoot = body.outputRoot
      folders = body.folders.map(folder => ({ ...responses.GetFolders[0], ...folder, id: nextId++, enabled: true, isCloud: /cloud/i.test(folder.path), filedCount: 0, baselineCount: 12 }))
      setStatus({ ...getStatus(), setupComplete: true, paused: body.finishAnyway, pauseReason: body.finishAnyway ? 'user' : null })
      return HttpResponse.json(setup())
    }),
    http.get('*/api/folders', () => HttpResponse.json(folders)),
    http.post('*/api/folders/check', async ({ request }) => {
      const error = denied(request); if (error) return error
      const body = await request.json() as Schema['CheckFolderRequest']
      return HttpResponse.json(check(body.path.trim()))
    }),
    http.post('*/api/system/pick-folder', ({ request }) => denied(request) ?? HttpResponse.json({ path: 'D:\\SlothBox-Sample\\New input' })),
    http.post('*/api/folders', async ({ request }) => {
      const error = denied(request); if (error) return error
      const body = await request.json() as Schema['CreateFolderRequest']
      const checked = check(body.path)
      if (!checked.accepted) return HttpResponse.json({ title: checked.reason }, { status: 400 })
      if (folders.some(folder => folder.path.toLowerCase() === body.path.toLowerCase())) return HttpResponse.json({ title: 'This folder is already watched.' }, { status: 409 })
      const folder: Schema['FolderDto'] = { id: nextId++, path: body.path, label: body.label || body.path.split(/[\\/]/).at(-1) || body.path,
        mode: checked.copyOnly ? 'copy' : body.mode, isCloud: checked.copyOnly, enabled: true, filedCount: 0, baselineCount: checked.existingFileCount, missing: false }
      folders.push(folder)
      if (body.tidyExisting) tidy(folder)
      return HttpResponse.json(folder)
    }),
    http.patch('*/api/folders/:id', async ({ request, params }) => {
      const error = denied(request); if (error) return error
      const folder = folders.find(item => String(item.id) === params.id)
      if (!folder) return missing()
      const body = await request.json() as Schema['UpdateFolderRequest']
      if (body.label != null) folder.label = body.label
      if (body.enabled != null) folder.enabled = body.enabled
      if (body.mode != null) folder.mode = folder.isCloud ? 'copy' : body.mode
      return HttpResponse.json(folder)
    }),
    http.delete('*/api/folders/:id', ({ request, params }) => {
      const error = denied(request); if (error) return error
      if (!folders.some(item => String(item.id) === params.id)) return missing()
      folders = folders.filter(item => String(item.id) !== params.id)
      return HttpResponse.json({ success: true, message: null })
    }),
    http.post('*/api/folders/:id/tidy-existing', ({ request, params }) => {
      const error = denied(request); if (error) return error
      const folder = folders.find(item => String(item.id) === params.id)
      return folder ? HttpResponse.json({ folderId: folder.id, queued: tidy(folder) }) : missing()
    }),
    http.get('*/api/phone', () => HttpResponse.json(phone)),
    http.post('*/api/phone/rotate', ({ request }) => {
      const error = denied(request); if (error) return error
      phoneVersion++
      phone = { ...phone, url: `http://127.0.0.1:5081/u/sample-token-${phoneVersion}`,
        qrSvg: responses.GetPhone.qrSvg.replace('Sample QR placeholder', `Sample QR placeholder ${phoneVersion}`) }
      return HttpResponse.json(phone)
    }),
  ]
}

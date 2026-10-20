import type { JsonOperation, JsonResponse, MockEvent, Schema } from '../api/types'

// Synthetic examples only. These paths are strings; mocks never access the filesystem.
export const fileId = '10000000-0000-4000-8000-000000000001'
const at = '2026-10-20T10:00:00Z'
const duplicateId = '10000000-0000-4000-8000-000000000002'
export const laziness = {
  receiptSeconds: 30, screenshotSeconds: 15, documentSeconds: 15, otherSeconds: 5,
} satisfies Schema['LazinessSettings']
export const status = {
  gemma: { ready: true, message: null },
  visionModel: { name: 'gemma4:e4b', present: true, loaded: true, onGpu: true },
  embeddingModel: { name: 'embeddinggemma', present: true, loaded: true, onGpu: false },
  database: { ready: true, message: null },
  queue: { waiting: 2, processing: 1 },
  paused: false, setupComplete: true, averageSecondsPerFileToday: 8,
} satisfies Schema['StatusDto']
export const activity = {
  fileId, at, status: 'filed', type: 'receipt', category: 'Food and drink',
  title: 'Sample lunch', newName: '2026-10-20_Sample-Cafe_120INR.jpg',
  folder: 'D:\\SlothBox-Sample\\Filed\\Receipts\\Food and drink',
  origin: 'phone', folderLabel: null, thumbUrl: `/api/files/${fileId}/thumb`, canUndo: true,
} satisfies Schema['ActivityItemDto']
const receipt = {
  vendor: 'Sample Cafe', date: '2026-10-20', amount: 120, currency: 'INR', paymentMethod: 'cash',
} satisfies Schema['ReceiptDto']
const details = {
  file: activity, originalName: 'sample.jpg', originalPath: 'D:\\SlothBox-Sample\\Incoming\\sample.jpg',
  currentPath: `${activity.folder}\\${activity.newName}`,
  caption: 'Synthetic lunch receipt', sizeBytes: 1024, categoryId: 1, reviewReason: null, receipt,
  history: [{
    id: 1, kind: 'move', fromPath: 'D:\\SlothBox-Sample\\Incoming\\sample.jpg',
    toPath: `${activity.folder}\\${activity.newName}`, status: 'done', at, error: null,
  }],
} satisfies Schema['FileDetailsDto']
const review = {
  ...details, file: { ...activity, status: 'review', canUndo: false },
  reviewReason: 'missing_amount', receipt: { ...receipt, amount: null },
} satisfies Schema['FileDetailsDto']
const category = {
  id: 1, type: 'receipt', name: 'Food and drink', description: 'Meals and groceries',
  folder: 'Food and drink', isSystem: false, fileCount: 1,
} satisfies Schema['CategoryDto']
const folder = {
  id: 1, path: 'D:\\SlothBox-Sample\\Incoming', label: 'Sample input', mode: 'copy',
  isCloud: false, enabled: true, filedCount: 1, baselineCount: 3, missing: false,
} satisfies Schema['FolderDto']
const settings = {
  startAtLogin: false, laziness, ollamaUrl: 'http://127.0.0.1:11436',
} satisfies Schema['SettingsDto']
const setup = {
  complete: true, outputRoot: 'D:\\SlothBox-Sample\\Filed',
  suggestedFolders: [{ path: folder.path, label: folder.label, mode: folder.mode }],
  status, nativePickerAvailable: true,
} satisfies Schema['SetupDto']
const phone = {
  enabled: true, url: 'http://127.0.0.1:5081/u/sample-token',
  qrSvg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><title>Sample QR placeholder</title><rect width="64" height="64" fill="#eef2ef"/><path d="M8 8h16v16H8zM40 8h16v16H40zM8 40h16v16H8zM40 40h8v8h8v8H40z" fill="#2f4d3c"/></svg>',
  firewallRulePresent: true,
} satisfies Schema['PhoneDto']
const success = { success: true, message: null } satisfies Schema['MutationResultDto']
const undone = {
  fileId, success: true, restoredPath: details.originalPath, reason: null,
} satisfies Schema['UndoResultDto']

export const responses = {
  GetStatus: status,
  Pause: { ...status, paused: true },
  Resume: status,
  GetActivity: { items: [activity], nextCursor: null },
  GetLazinessStats: {
    filesFiled: 1, minutesSaved: 0.5, receiptsLogged: 1,
    receiptTotals: [{ currency: 'INR', amount: 120, count: 1 }], duplicatesCaught: 0,
    days: [{ date: '2026-10-20', count: 1 }], secondsPerType: laziness,
  },
  GetFile: details,
  OpenFile: success,
  RevealFile: success,
  UndoFile: undone,
  Refile: details,
  UndoLast: undone,
  UndoSince: { undone: 1, results: [undone] },
  GetReview: [{ file: review, reason: 'missing_amount' }],
  SkipReview: success,
  Search: { items: [{ file: details, score: 0.95, matchedWords: true, matchedMeaning: true }] },
  GetReceipts: {
    month: '2026-10', items: [{ fileId, ...receipt, categoryId: 1, category: category.name }],
    totals: [{ currency: 'INR', amount: 120, count: 1 }],
    categories: [{ categoryId: 1, category: category.name, currency: 'INR', amount: 120 }],
  },
  GetCategories: [category],
  CreateCategory: category,
  UpdateCategory: category,
  DeleteCategory: success,
  GetFolders: [folder],
  CheckFolder: { accepted: true, reason: null, copyOnly: false, existingFileCount: 3 },
  CreateFolder: folder,
  UpdateFolder: folder,
  TidyExisting: { folderId: 1, queued: 3 },
  DeleteFolder: success,
  PickFolder: { path: folder.path },
  GetSettings: settings,
  UpdateSettings: settings,
  GetSetup: setup,
  CompleteSetup: setup,
  GetPhone: phone,
  RotatePhone: { ...phone, url: 'http://127.0.0.1:5081/u/rotated-sample-token' },
  GetIgnored: [{ sha256: 'a'.repeat(64), fileName: 'sample.jpg', at, reason: 'Previously undone' }],
  AllowIgnored: success,
  RebuildSearchData: { queued: 1 },
  UploadPhoneFile: { fileName: 'sample.jpg', received: true },
} satisfies { [K in JsonOperation]: JsonResponse<K> }

export const events = [
  { event: 'queue.changed', data: { waiting: 2, processing: 1 } },
  { event: 'file.filed', data: activity },
  { event: 'file.review', data: { ...review.file, reason: 'missing_amount' } },
  { event: 'file.duplicate', data: {
    ...activity, fileId: duplicateId, thumbUrl: `/api/files/${duplicateId}/thumb`,
    status: 'duplicate', duplicateOf: fileId,
  } },
  { event: 'file.undone', data: { fileId } },
  { event: 'file.refiled', data: activity },
  { event: 'status.changed', data: status },
  { event: 'folder.tidy-progress', data: { folderId: 1, done: 1, total: 3 } },
] satisfies MockEvent[]

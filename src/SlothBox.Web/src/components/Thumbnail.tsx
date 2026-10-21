import { useState } from 'react'
import { File, FileText, Image, Receipt } from 'lucide-react'
import type { Schema } from '../api/types'

export function Thumbnail({ file, preview = false }: { file: Schema['ActivityItemDto']; preview?: boolean }) {
  const [failed, setFailed] = useState<string | null>(null)
  const imageFile = /\.(jpe?g|png|gif|webp|bmp)$/i.test(file.newName)
  const url = preview ? (imageFile ? `/api/files/${encodeURIComponent(file.fileId)}/preview` : null) : file.thumbUrl
  const Icon = file.type === 'receipt' ? Receipt : file.type === 'screenshot' ? Image : file.type === 'document' ? FileText : File
  return <div className={preview ? 'file-preview' : 'thumbnail'}>
    {url && failed !== url
      ? <img src={url} alt={file.title} onError={() => setFailed(url)} />
      : <Icon aria-label={file.title || 'File'} role="img" size={preview ? 56 : 24} strokeWidth={1.75} />}
  </div>
}

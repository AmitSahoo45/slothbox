import { useSearchParams } from 'react-router-dom'
import type { Schema } from '../api/types'
import { useFileActions } from '../api/file-actions'
import { Button } from './Button'
import { Thumbnail } from './Thumbnail'
import { originLabel, relativeTime, statusNames, typeFolders } from './format'

export function ActivityRow({ file, arriving }: { file: Schema['ActivityItemDto']; arriving?: boolean }) {
  const [params, setParams] = useSearchParams()
  const { undo } = useFileActions()
  const split = Math.ceil(file.newName.length * .6)
  return <li className={`activity-row ${arriving ? 'arriving' : ''}`}>
    <button type="button" className="activity-open" aria-label={`View ${file.newName}`}
      onClick={() => { const next = new URLSearchParams(params); next.set('file', file.fileId); setParams(next) }}>
      <Thumbnail file={file} />
      <span className="activity-copy">
        <span className="file-name" title={file.newName} aria-label={file.newName}>
          <span className="sr-only">{file.newName}</span>
          <span className="file-name-start" aria-hidden="true">{file.newName.slice(0, split)}</span>
          <span className="file-name-end" aria-hidden="true"><span>{file.newName.slice(split)}</span></span>
        </span>
        <span className="activity-meta">
          <span className={`chip type-${file.type}`}>{typeFolders[file.type]} / {file.category}</span>
          <span>from {originLabel(file)}, {relativeTime(file.at)}</span>
        </span>
      </span>
      <span className={`chip status-${file.status}`}>{statusNames[file.status]}</span>
    </button>
    <Button variant="quiet" busy={undo.isPending} disabled={!file.canUndo} onClick={() => undo.mutate(file.fileId)}>Undo</Button>
  </li>
}

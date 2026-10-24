import type { Schema } from '../api/types'
import { Banner } from '../components/Banner'
import { Button } from '../components/Button'
import { useToasts } from '../components/toast-context'

export function Banners({ status, error, checking, disconnected, busy, onCheck, onResume }:
  { status?: Schema['StatusDto']; error: Error | null; checking: boolean; disconnected: boolean;
    busy: boolean; onCheck: () => void; onResume: () => void }) {
  const { notify } = useToasts()
  const waiting = Number(status?.queue.waiting ?? 0)
  const driveFull = status?.pauseReason === 'drive_full'
  const gemmaOffline = status?.pauseReason === 'gemma_offline'
  const check = <Button busy={checking} onClick={onCheck}>Check again</Button>
  const resume = <Button busy={busy} onClick={onResume}>Resume</Button>
  const copy = async (command: string) => {
    try { await navigator.clipboard.writeText(command); notify({ message: 'Command copied' }) }
    catch { notify({ message: 'Could not copy the command. Select it and copy it yourself.', tone: 'error' }) }
  }
  return <div className="banners">
    {error && <Banner tone="error" action={check}>Could not check SlothBox. {error.message}</Banner>}
    {disconnected && <Banner tone="info">Live updates disconnected. Reconnecting…</Banner>}
    {status && <>
      {!status.database.ready && <Banner tone="error" action={check}>
        The database is offline. Nothing will move until it's back.
      </Banner>}
      {gemmaOffline && <Banner action={check}>
        Gemma is offline, {waiting} {waiting === 1 ? 'file' : 'files'} waiting.
      </Banner>}
      {!status.gemma.ready && !gemmaOffline && <Banner action={check}>
        Gemma is offline. {waiting} {waiting === 1 ? 'file is' : 'files are'} waiting. Start Docker Desktop and SlothBox will carry on by itself.
      </Banner>}
      {[{ model: status.visionModel, label: 'Gemma' }, { model: status.embeddingModel, label: 'EmbeddingGemma' }]
        .filter(({ model }) => !model.present).map(({ model, label }) => {
          const command = `docker exec slothbox-ollama ollama pull ${model.name}`
          return <Banner key={label} action={<Button onClick={() => void copy(command)}>Copy command</Button>}>
            <p>{label} isn't installed.</p><code>{command}</code>
          </Banner>
        })}
      {driveFull && <Banner tone="error" action={resume}>
        The output drive is full. SlothBox paused itself. Free some space to continue.
      </Banner>}
      {status.paused && !driveFull && !gemmaOffline && <Banner tone="neutral" action={resume}>
        SlothBox is paused. {waiting} {waiting === 1 ? 'file is' : 'files are'} waiting.
      </Banner>}
    </>}
  </div>
}

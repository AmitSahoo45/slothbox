import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Navigate, useNavigate } from 'react-router-dom'
import { request } from '../../api/client'
import type { Schema } from '../../api/types'
import { Button } from '../../components/Button'
import { QueryError } from '../../components/QueryError'
import { useToasts } from '../../components/toast-context'
import { AddFolder } from '../Folders/AddFolder'
import { setupQuery } from '../../api/setup-query'

const canonical = (path: string) => path.trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()

export function Setup() {
  const setup = useQuery(setupQuery)
  if (setup.error) return <QueryError error={setup.error} retry={() => { void setup.refetch() }} label="Retry setup" />
  if (!setup.data) return <p role="status">Loading setup…</p>
  if (setup.data.complete) return <Navigate to="/" replace />
  return <SetupSteps initial={setup.data} />
}
function SetupSteps({ initial }: { initial: Schema['SetupDto'] }) {
  const [step, setStep] = useState(1)
  const [outputRoot, setOutput] = useState(initial.outputRoot)
  const [folders, setFolders] = useState(initial.suggestedFolders.map(folder => ({ ...folder, selected: true })))
  const [adding, setAdding] = useState(false)
  const title = useRef<HTMLHeadingElement>(null)
  const client = useQueryClient()
  const navigate = useNavigate()
  const { notify } = useToasts()
  const checks = useQuery({ ...setupQuery, enabled: step === 3, refetchInterval: step === 3 ? 10000 : false })
  const refetchChecks = checks.refetch
  useEffect(() => { title.current?.focus(); if (step === 3) void refetchChecks() }, [step, refetchChecks])
  const status = checks.data?.status ?? initial.status
  const allReady = status.gemma.ready && status.visionModel.present && status.embeddingModel.present && status.database.ready
  // Step 1 asks the API's output-folder rules (7.4.1) before anything is saved; finishing checks them all again.
  const checkOutput = useMutation({ mutationFn: async (path: string) => {
    const result = await request('post', '/api/setup/check-output', { body: { path } })
    if (!result.accepted) throw new Error(result.reason || 'SlothBox cannot put sorted files in this folder.')
  }, onSuccess: () => setStep(2) })
  const pick = useMutation({ mutationFn: () => request('post', '/api/system/pick-folder'), onSuccess: result => { if (result.path) { setOutput(result.path); checkOutput.reset() } } })
  const next = useMutation({ mutationFn: async () => {
    const selected = []
    const seen = new Set<string>()
    for (const folder of folders) {
      if (!folder.selected) { selected.push(folder); continue }
      const path = canonical(folder.path)
      const output = canonical(outputRoot)
      if (path === output || path.startsWith(`${output}\\`) || output.startsWith(`${path}\\`)) throw new Error('Watched folders and the output folder cannot contain each other.')
      if (seen.has(path)) throw new Error('Choose each watched folder only once.')
      if ([...seen].some(other => path.startsWith(`${other}\\`) || other.startsWith(`${path}\\`))) throw new Error('Watched folders cannot contain each other.')
      seen.add(path)
      const check = await request('post', '/api/folders/check', { body: { path: folder.path } })
      if (!check.accepted) throw new Error(check.reason || 'This folder cannot be watched.')
      selected.push({ ...folder, mode: check.copyOnly ? 'copy' as const : folder.mode })
    }
    return selected
  }, onSuccess: selected => { setFolders(selected); setStep(3) } })
  const finish = useMutation({ mutationFn: async (finishAnyway: boolean) => {
    const result = await request('post', '/api/setup/complete', { body: { outputRoot: outputRoot.trim(),
      folders: folders.filter(folder => folder.selected).map(({ path, label, mode }) => ({ path, label, mode })), finishAnyway } })
    if (!result.complete) throw new Error('Setup was not completed. Check the settings and try again.')
    return result
  }, onError: () => {
    // A 409 can mean setup was finished elsewhere; a fresh setup read then leaves this page.
    void client.invalidateQueries({ queryKey: ['setup'] })
  }, onSuccess: async result => {
    await client.cancelQueries({ queryKey: ['status'] })
    client.setQueryData(['status'], result.status)
    client.setQueryData(['setup'], result)
    void client.invalidateQueries({ queryKey: ['folders'] })
    navigate('/', { replace: true })
  } })
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); notify({ message: 'Command copied' }) }
    catch { notify({ message: 'Could not copy the command. Select it and copy it yourself.', tone: 'error' }) }
  }
  const outputValid = /^[a-z]:[\\/].+/i.test(outputRoot.trim()) && !/^[a-z]:[\\/]*$/i.test(outputRoot.trim())
  return <section className="content-page setup-page">
    <p className="step-count">Step {step} of 3</p>
    <h2 ref={title} tabIndex={-1}>{step === 1 ? 'Where should SlothBox put sorted files?' : step === 2 ? 'Which folders should SlothBox watch?' : 'Checking that SlothBox can think'}</h2>
    {step === 1 && <>
      <div className="output-picker"><label className="field">Output folder<input value={outputRoot} disabled={checkOutput.isPending} onChange={event => { setOutput(event.target.value); checkOutput.reset() }} /></label>
        {initial.nativePickerAvailable && <Button busy={pick.isPending} disabled={checkOutput.isPending} onClick={() => pick.mutate()}>Browse…</Button>}</div>
      <p>This can't be changed later in this version.</p>
      {!outputValid && <p className="field-hint">Choose an absolute folder on a drive, such as D:\SlothBox.</p>}
      {pick.error && <p role="alert">{pick.error.message}</p>}
      {checkOutput.error && <p role="alert" className="query-error">{checkOutput.error.message}</p>}
      <div className="form-actions"><Button variant="primary" busy={checkOutput.isPending} disabled={!outputValid || pick.isPending} onClick={() => checkOutput.mutate(outputRoot.trim())}>Continue</Button></div>
    </>}
    {step === 2 && <>
      <div className="setup-folders">{folders.map((folder, index) => <label key={`${folder.path}-${index}`} className="setup-folder">
        <input type="checkbox" checked={folder.selected} disabled={next.isPending} onChange={event => setFolders(previous => previous.map((item, i) => i === index ? { ...item, selected: event.target.checked } : item))} />
        <span><strong>{folder.label}</strong><code>{folder.path}</code></span><span>{folder.mode === 'copy' ? 'Copy only' : 'Move'}</span>
      </label>)}</div>
      <Button disabled={next.isPending} onClick={() => setAdding(true)}>Add folder</Button>
      <p className="field-hint">Files already in these folders stay where they are. You can tidy them later from Folders.</p>
      {next.error && <p role="alert" className="query-error">{next.error.message}</p>}
      <div className="form-actions"><Button disabled={next.isPending} onClick={() => setStep(1)}>Back</Button><Button variant="primary" busy={next.isPending} onClick={() => next.mutate()}>Continue</Button></div>
    </>}
    {step === 3 && <>
      <ul className="setup-checks">
        {[{ name: 'Gemma', ready: status.gemma.ready && status.visionModel.present, model: status.visionModel, message: status.gemma.message },
          { name: 'EmbeddingGemma', ready: status.embeddingModel.present, model: status.embeddingModel, message: null },
          { name: 'Database', ready: status.database.ready, model: null, message: status.database.message }].map(check => <li key={check.name}>
          <span className={check.ready ? 'check-ready' : 'check-waiting'}>{check.ready ? '✓' : '!'}</span><div><strong>{check.name}</strong>{check.model && <span> ({check.model.name})</span>}
            <p>{check.ready ? check.model?.loaded && check.model.onGpu ? 'Ready, on GPU' : 'Ready' : check.message || (check.model && !check.model.present ? 'Not installed' : 'Not ready')}</p>
            {check.model && !check.model.present && <div className="copy-command"><code>docker exec slothbox-ollama ollama pull {check.model.name}</code><Button onClick={() => void copy(`docker exec slothbox-ollama ollama pull ${check.model!.name}`)}>Copy</Button></div>}
          </div></li>)}
      </ul>
      {checks.error && <p role="alert" className="query-error">{checks.error.message}</p>}
      {finish.error && <p role="alert" className="query-error">{finish.error.message}</p>}
      <div className="form-actions"><Button disabled={finish.isPending} onClick={() => setStep(2)}>Back</Button><Button busy={checks.isFetching} disabled={finish.isPending} onClick={() => { void checks.refetch() }}>Check again</Button>
        <Button variant="primary" busy={finish.isPending} disabled={!allReady || Boolean(checks.error)} onClick={() => finish.mutate(false)}>Finish setup</Button></div>
      <Button variant="quiet" disabled={finish.isPending} onClick={() => finish.mutate(true)}>Finish anyway</Button><p className="field-hint">Finish anyway keeps the queue paused.</p>
    </>}
    {adding && <AddFolder nativePicker={initial.nativePickerAvailable} setup onClose={() => setAdding(false)} onAdd={async folder => {
      if (folders.some(existing => canonical(existing.path) === canonical(folder.path))) throw new Error('This folder is already in setup.')
      setFolders(previous => [...previous, { path: folder.path, label: folder.label || folder.path.split(/[\\/]/).filter(Boolean).at(-1) || folder.path, mode: folder.mode, selected: true }])
    }} />}
  </section>
}

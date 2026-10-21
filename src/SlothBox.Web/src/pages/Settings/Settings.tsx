import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { request } from '../../api/client'
import { requireSuccess } from '../../api/file-actions'
import { statusQuery } from '../../api/queries'
import type { Schema } from '../../api/types'
import { useTheme, type Theme } from '../../app/theme'
import { Button } from '../../components/Button'
import { QueryError } from '../../components/QueryError'
import { useToasts } from '../../components/toast-context'
import { displayTime } from '../../components/format'

const durations = { receiptSeconds: 'receipt', screenshotSeconds: 'screenshot', documentSeconds: 'document', otherSeconds: 'other' } as const
type SecondsKey = keyof typeof durations

export function Settings() {
  const { theme, choose } = useTheme()
  const settings = useQuery({ queryKey: ['settings'], queryFn: ({ signal }) => request('get', '/api/settings', { signal }) })
  return <div className="content-page settings-page">
    <section className="settings-section" aria-labelledby="appearance-heading"><h2 id="appearance-heading">Appearance</h2>
      <fieldset className="segmented"><legend className="sr-only">Theme</legend>
        {(['system', 'light', 'dark'] as Theme[]).map(value => <label key={value}>
          <input type="radio" name="theme" checked={theme === value} onChange={() => choose(value)} /><span>{value[0].toUpperCase() + value.slice(1)}</span>
        </label>)}
      </fieldset>
    </section>
    {settings.isPending && <p role="status">Loading settings…</p>}
    {settings.error && <QueryError error={settings.error} retry={() => { void settings.refetch() }} label="Retry settings" />}
    {settings.data && <SettingsForm settings={settings.data} unavailable={!!settings.error} />}
    <IgnoredFiles />
    <section className="settings-section" aria-labelledby="about-heading"><h2 id="about-heading">About</h2>
      <a href="https://github.com/AmitSahoo45/slothbox" target="_blank" rel="noreferrer">SlothBox on GitHub</a>
    </section>
  </div>
}

function SettingsForm({ settings, unavailable }: { settings: Schema['SettingsDto']; unavailable: boolean }) {
  const [draft, setDraft] = useState<Partial<Omit<Schema['UpdateSettingsRequest'], 'laziness'>>>({})
  const [seconds, setSeconds] = useState<Partial<Record<SecondsKey, string>>>({})
  const values = { ...settings, ...draft }
  const durationValues = Object.fromEntries(Object.keys(durations).map(key => [key, seconds[key as SecondsKey] ?? String(settings.laziness[key as SecondsKey])])) as Record<SecondsKey, string>
  const validDurations = Object.values(durationValues).every(value => value.trim() !== '' && Number.isInteger(Number(value)) && Number(value) >= 0 && Number(value) <= 2147483647)
  let validAddress = false
  try { validAddress = ['http:', 'https:'].includes(new URL(values.ollamaUrl).protocol) } catch { /* Show validation below. */ }
  const dirty = Object.keys(draft).length > 0 || Object.keys(seconds).length > 0
  const client = useQueryClient()
  const { notify } = useToasts()
  const save = useMutation({
    mutationFn: () => request('put', '/api/settings', { body: { ...values, ollamaUrl: values.ollamaUrl.trim(),
      laziness: { receiptSeconds: Number(durationValues.receiptSeconds), screenshotSeconds: Number(durationValues.screenshotSeconds), documentSeconds: Number(durationValues.documentSeconds), otherSeconds: Number(durationValues.otherSeconds) } } }),
    onSuccess: async data => {
      await client.cancelQueries({ queryKey: ['settings'] })
      client.setQueryData(['settings'], data)
      setDraft({}); setSeconds({})
      void client.invalidateQueries({ queryKey: ['stats'] })
      void client.invalidateQueries({ queryKey: ['status'] })
      notify({ message: 'Settings saved' })
    }, onError: error => notify({ message: error.message, tone: 'error' }),
  })
  return <form onSubmit={event => { event.preventDefault(); if (!unavailable && validAddress && validDurations && dirty && !save.isPending) save.mutate() }}>
    <fieldset className="plain-fields" disabled={save.isPending}>
      <section className="settings-section" aria-labelledby="startup-heading"><h2 id="startup-heading">Startup</h2>
        <label className="switch-label"><input type="checkbox" role="switch" checked={values.startAtLogin} onChange={event => setDraft({ ...draft, startAtLogin: event.target.checked })} />Start SlothBox when I sign in</label>
      </section>
      <section className="settings-section" aria-labelledby="ai-heading"><h2 id="ai-heading">AI</h2>
        <label className="field">Ollama address<input type="url" required value={values.ollamaUrl} onChange={event => setDraft({ ...draft, ollamaUrl: event.target.value })} aria-invalid={!validAddress || undefined} aria-describedby={!validAddress ? 'address-error' : undefined} /></label>
        {!validAddress && <p id="address-error" className="field-error">Enter a complete http or https address.</p>}
        <ModelStatus addressChanged={draft.ollamaUrl !== undefined && draft.ollamaUrl !== settings.ollamaUrl} />
      </section>
      <section className="settings-section" aria-labelledby="laziness-heading"><h2 id="laziness-heading">Laziness meter</h2>
        <p className="field-hint">Estimated time saved for each file.</p>
        <div className="duration-fields">{(Object.keys(durations) as SecondsKey[]).map(key => <label key={key} className="field">Seconds per {durations[key]}
          <input type="number" required min={0} max={2147483647} step={1} value={durationValues[key]} onChange={event => setSeconds({ ...seconds, [key]: event.target.value })} aria-describedby={!validDurations ? 'duration-error' : undefined} />
        </label>)}</div>
        {!validDurations && <p id="duration-error" className="field-error">Use whole seconds from 0 to 2,147,483,647.</p>}
      </section>
      <div className="settings-save"><Button type="submit" variant="primary" busy={save.isPending} disabled={unavailable || !validAddress || !validDurations || !dirty}>Save settings</Button>
        {dirty && <span className="field-hint">Unsaved changes</span>}</div>
    </fieldset>
  </form>
}

function ModelStatus({ addressChanged }: { addressChanged: boolean }) {
  const status = useQuery(statusQuery)
  const { notify } = useToasts()
  const rebuild = useMutation({ mutationFn: () => request('post', '/api/search-data/rebuild'),
    onSuccess: data => notify({ message: `Queued ${data.queued} ${Number(data.queued) === 1 ? 'file' : 'files'} for search data rebuild` }),
    onError: error => notify({ message: error.message, tone: 'error' }),
  })
  return <>
    {status.data && <ul className="model-list">{[status.data.visionModel, status.data.embeddingModel].map((model, index) => <li key={index}>
      <span>{model.name}</span><span className="field-hint">{!status.data.gemma.ready ? 'Service offline' : !model.present ? 'Not installed' : !model.loaded ? 'Installed, not loaded' : model.onGpu ? 'Loaded on GPU' : 'Loaded on CPU'}</span>
    </li>)}</ul>}
    {status.error && <p className="field-error" role="alert">{status.error.message}</p>}
    {addressChanged && <p className="field-hint">Save settings before checking the new address.</p>}
    <div className="form-actions"><Button busy={status.isFetching} disabled={addressChanged} onClick={() => { void status.refetch() }}>Check again</Button>
      <Button busy={rebuild.isPending} onClick={() => rebuild.mutate()}>Rebuild missing search data</Button></div>
  </>
}

function IgnoredFiles() {
  const query = useQuery({ queryKey: ['ignored'], queryFn: ({ signal }) => request('get', '/api/ignored', { signal }) })
  const client = useQueryClient()
  const { notify } = useToasts()
  const allow = useMutation({ mutationFn: async (sha256: string) => requireSuccess(await request('delete', '/api/ignored/{sha256}', { path: { sha256 } })),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['ignored'] }); notify({ message: 'File allowed again' }) },
    onError: error => notify({ message: error.message, tone: 'error' }),
  })
  return <section className="settings-section" aria-labelledby="ignored-heading"><h2 id="ignored-heading">Ignored files</h2>
    <p className="field-hint">Undone files SlothBox will not refile unless you allow them again.</p>
    {query.isPending && <p role="status">Loading ignored files…</p>}
    {query.error && <QueryError error={query.error} retry={() => { void query.refetch() }} label="Retry ignored files" />}
    {query.data?.length === 0 && <p className="empty-state">No ignored files.</p>}
    <ul className="ignored-list">{query.data?.map(file => <li key={file.sha256}>
      <div><code>{file.fileName}</code><time dateTime={file.at}>{new Date(file.at).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}, {displayTime(file.at)}</time></div>
      <Button aria-label={`Allow again ${file.fileName}`} busy={allow.isPending && allow.variables === file.sha256} disabled={allow.isPending} onClick={() => allow.mutate(file.sha256)}>Allow again</Button>
    </li>)}</ul>
  </section>
}

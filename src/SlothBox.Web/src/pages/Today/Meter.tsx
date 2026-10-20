import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import * as Popover from '@radix-ui/react-popover'
import { request } from '../../api/client'
import { Button } from '../../components/Button'
import { HangingBars } from '../../components/HangingBars'
import { estimatedTime, money } from '../../components/format'
import { QueryError } from '../../components/QueryError'

const ranges = { today: 'Today', week: 'This week', all: 'All time' } as const
export function Meter() {
  const [range, setRange] = useState<keyof typeof ranges>('week')
  const stats = useQuery({
    queryKey: ['stats', 'laziness', range],
    queryFn: ({ signal }) => request('get', '/api/stats/laziness', { query: { range }, signal }),
  })
  const data = stats.data
  const start = range === 'all' ? 'Since you set it up,' : ranges[range]
  return <section className="meter" aria-label="Time saved">
    {stats.isPending && <p>Loading what SlothBox has done…</p>}
    {stats.error && <QueryError error={stats.error} retry={() => { void stats.refetch() }} label="Retry statistics" />}
    {data && <>
      <div className="meter-sentence">
        {Number(data.filesFiled) === 0
          ? <p>SlothBox hasn't filed anything {range === 'week' ? 'this week' : range === 'today' ? 'today' : 'yet'}. It's waiting, patiently.</p>
          : <><p>{start} SlothBox filed {data.filesFiled} {Number(data.filesFiled) === 1 ? 'file' : 'files'} for you.</p>
            <p>That's about {estimatedTime(data.minutesSaved)} of doing nothing.</p></>}
      </div>
      <p className="meter-details">{data.receiptsLogged} {Number(data.receiptsLogged) === 1 ? 'receipt' : 'receipts'} logged
        {data.receiptTotals.length > 0 && `, ${data.receiptTotals.map(total => money(total.amount, total.currency)).join(' and ')} in total`}.
        {' '}{data.duplicatesCaught} {Number(data.duplicatesCaught) === 1 ? 'duplicate' : 'duplicates'} caught.</p>
    </>}
    <div className="meter-controls">
      <Popover.Root>
        <Popover.Trigger asChild><Button variant="quiet" disabled={!data}>How we count</Button></Popover.Trigger>
        <Popover.Portal><Popover.Content className="popover" sideOffset={8} aria-label="How we count">
          <p>Time saved is an estimate for each file:</p>
          {data && <ul className="plain-list">
            <li>Receipt: {data.secondsPerType.receiptSeconds} seconds</li>
            <li>Screenshot: {data.secondsPerType.screenshotSeconds} seconds</li>
            <li>Document: {data.secondsPerType.documentSeconds} seconds</li>
            <li>Other: {data.secondsPerType.otherSeconds} seconds</li>
          </ul>}
          <Popover.Close asChild><Button variant="quiet">Close</Button></Popover.Close>
        </Popover.Content></Popover.Portal>
      </Popover.Root>
      <fieldset className="segmented"><legend className="sr-only">Meter range</legend>
        {Object.entries(ranges).map(([key, label]) => <label key={key}>
          <input type="radio" name="meter-range" checked={key === range} onChange={() => setRange(key as keyof typeof ranges)} />
          <span>{label}</span>
        </label>)}
      </fieldset>
    </div>
    {data && <HangingBars days={data.days} />}
  </section>
}

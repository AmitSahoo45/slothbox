import { ActivityRow } from '../../components/ActivityRow'
import { Button } from '../../components/Button'
import { QueryError } from '../../components/QueryError'
import { useActivity } from './use-activity'
import { UndoSince } from './UndoSince'
import { PhoneQr } from './PhoneQr'

export function ActivityFeed() {
  const feed = useActivity()
  return <section className="activity-section" aria-labelledby="activity-title">
    <div className="section-heading"><h2 id="activity-title">Activity</h2><UndoSince /></div>
    <p className="sr-only" role="status">{feed.reviewNotice}</p>
    {feed.pending.length > 0 && <div className="new-files"><Button variant="primary" onClick={feed.reveal}>
      {feed.pending.length} new {feed.pending.length === 1 ? 'file' : 'files'}
    </Button></div>}
    {feed.query.isPending && <p>Loading activity…</p>}
    {feed.query.error && <QueryError error={feed.query.error} retry={() => { void feed.query.refetch() }} label="Retry activity" />}
    {feed.loaded && feed.rows.length === 0 && !feed.query.error
      ? <div className="empty-state"><p>Nothing filed yet. Save a file to a watched folder, take a screenshot, or send one from your phone.</p><PhoneQr /></div>
      : <ul className="activity-list" aria-label="Activity">{feed.rows.map(file =>
        <ActivityRow key={file.fileId} file={file} arriving={feed.arriving.includes(file.fileId)} />)}</ul>}
    {feed.query.hasNextPage && <Button className="show-more" busy={feed.query.isFetchingNextPage}
      onClick={() => { void feed.query.fetchNextPage() }}>Show more</Button>}
  </section>
}

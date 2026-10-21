import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { reviewQuery } from '../../api/queries'
import { Meter } from './Meter'
import { ActivityFeed } from './ActivityFeed'

export function Today() {
  const review = useQuery(reviewQuery)
  const count = review.data?.length ?? 0
  return <div className="today-page">
    <Meter />
    {count > 0 && <div className="review-banner">
      <p>{count} {count === 1 ? 'file needs' : 'files need'} you.</p>
      <Link className="button" to="/review">Review now</Link>
    </div>}
    <ActivityFeed />
  </div>
}

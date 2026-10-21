import type { Schema } from '../api/types'
import { displayDate, localDate } from './format'

export function HangingBars({ days, now = new Date() }: { days: Schema['DayCountDto'][]; now?: Date }) {
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (now.getDay() + 6) % 7)
  const counts = new Map(days.map(day => [day.date, Number(day.count)]))
  const today = localDate(now)
  const week = Array.from({ length: 7 }, (_, index) => {
    const date = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + index)
    const key = localDate(date)
    return { date: key, label: date.toLocaleDateString('en-GB', { weekday: 'short' }),
      count: counts.get(key) ?? 0, future: key > today, today: key === today }
  })
  const maximum = Math.max(1, ...week.filter(day => !day.future).map(day => day.count))
  return <div className="hanging-bars">
    <div className="bar-chart" aria-label="Files filed this week chart" role="img">
      {week.map((day, index) => <div key={day.date} className="bar-day" aria-hidden="true"
        data-day={index} data-count={day.count} data-today={day.today} data-future={day.future}>
        <span className="bar-label">{day.label}</span>
        <div className="bar-branch">
          {!day.future && <><span className="bar" style={{ height: day.count ? `${day.count / maximum * 112}px` : '4px' }} />
            <span className="bar-count">{day.count}</span></>}
        </div>
      </div>)}
    </div>
    <table className="sr-only">
      <caption>Files filed this week</caption>
      <thead><tr><th scope="col">Day</th><th scope="col">Files filed</th></tr></thead>
      <tbody>{week.map(day => <tr key={day.date}>
        <th scope="row">{day.label}, {displayDate(day.date)}</th><td>{day.future ? 'Not yet' : day.count}</td>
      </tr>)}</tbody>
    </table>
  </div>
}

import { NavLink } from 'react-router-dom'
import { Database, Leaf, ListTodo, Pause, Play, Sprout } from 'lucide-react'
import type { Schema } from '../api/types'
import { Button } from '../components/Button'
import { navigation } from './navigation'
import { useCompactSidebar } from './use-compact-sidebar'

export function Sidebar({ status, reviewCount, busy, onControl }:
  { status?: Schema['StatusDto']; reviewCount?: number; busy: boolean; onControl: (action: 'pause' | 'resume') => void }) {
  const compact = useCompactSidebar()
  const waiting = status ? Number(status.queue.waiting) : undefined
  const gemma = `Gemma ${status ? status.gemma.ready ? 'ready' : 'offline' : 'not checked'}`
  const database = `Database ${status ? status.database.ready ? 'ready' : 'offline' : 'not checked'}`
  const action = status?.paused ? 'Resume' : 'Pause'
  const ControlIcon = status?.paused ? Play : Pause
  return <aside className="sidebar" aria-label="Sidebar" data-collapsed={compact}>
    <NavLink to="/" className="brand" aria-label="SlothBox">
      <Leaf aria-hidden="true" size={24} strokeWidth={1.75} /><span className="sidebar-label">SlothBox</span>
    </NavLink>
    <nav aria-label="Main">
      {navigation.map(({ path, label, icon: Icon }) => <NavLink key={path} to={path} end={path === '/'}
        title={compact ? label : undefined} className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
        <Icon aria-hidden="true" size={20} strokeWidth={1.75} />
        <span className="sidebar-label">{label}</span>
        {path === '/review' && reviewCount !== undefined && reviewCount > 0
          ? <span className="review-count">{reviewCount}<span className="sr-only"> files need review</span></span> : null}
      </NavLink>)}
    </nav>
    <div className="status-block" aria-label="SlothBox status">
      <div aria-live="polite" aria-atomic="true">
        <p title={compact ? gemma : undefined} className={status?.gemma.ready ? 'service-ready' : 'service-waiting'}>
          <Sprout aria-hidden="true" size={20} strokeWidth={1.75} /><span className="sidebar-label">{gemma}</span>
        </p>
        <p title={compact ? database : undefined} className={status?.database.ready ? 'service-ready' : 'service-waiting'}>
          <Database aria-hidden="true" size={20} strokeWidth={1.75} /><span className="sidebar-label">{database}</span>
        </p>
        <p title={compact && waiting !== undefined ? `${waiting} files waiting` : undefined}>
          <ListTodo aria-hidden="true" size={20} strokeWidth={1.75} />
          <span className="sidebar-label">{waiting === undefined ? 'Checking queue…' : `${waiting} ${waiting === 1 ? 'file' : 'files'} waiting`}</span>
        </p>
      </div>
      <Button className="queue-control" busy={busy} disabled={!status} title={compact ? action : undefined}
        onClick={() => onControl(status?.paused ? 'resume' : 'pause')}>
        <ControlIcon aria-hidden="true" size={20} strokeWidth={1.75} /><span className="sidebar-label">{action}</span>
      </Button>
    </div>
  </aside>
}

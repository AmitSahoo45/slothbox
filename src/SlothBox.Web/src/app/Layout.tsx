import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Navigate, useLocation } from 'react-router-dom'
import { reviewQuery, statusQuery, useQueueControl } from '../api/queries'
import { useServerEvents } from '../api/events'
import { useToasts } from '../components/toast-context'
import { Sidebar } from './Sidebar'
import { Banners } from './Banners'
import { navigation } from './navigation'
import { Shortcuts } from './Shortcuts'
import { Today } from '../pages/Today/Today'
import { Review } from '../pages/Review/Review'
import { FileDrawer } from '../components/FileDrawer/FileDrawer'
import { Search } from '../pages/Search/Search'
import { Receipts } from '../pages/Receipts/Receipts'
import { Setup } from '../pages/Setup/Setup'
import { Folders } from '../pages/Folders/Folders'
import { Categories } from '../pages/Categories/Categories'
import { Settings } from '../pages/Settings/Settings'

export function Layout() {
  const status = useQuery(statusQuery)
  const review = useQuery({ ...reviewQuery, enabled: status.data?.setupComplete === true })
  const control = useQueueControl()
  const { disconnected } = useServerEvents()
  const { notify } = useToasts()
  const { pathname } = useLocation()
  const isSetup = pathname === '/setup'
  const page = navigation.find(item => item.path === pathname)
  const title = isSetup ? 'Set up SlothBox' : page?.label ?? 'Page not found'
  useEffect(() => { document.title = `${title} | SlothBox` }, [title])
  const changeQueue = (action: 'pause' | 'resume') => {
    control.mutate(action, {
      onSuccess: () => notify({ message: action === 'pause' ? 'Paused' : 'Resumed' }),
      onError: error => notify({ message: error.message, tone: 'error' }),
    })
  }
  if (status.data?.setupComplete === false && !isSetup) return <Navigate to="/setup" replace />
  return <div className="app-shell">
    <a className="skip-link" href="#main-content">Skip to content</a>
    {!isSetup && <Sidebar status={status.data} reviewCount={review.data?.length}
      busy={control.isPending} onControl={changeQueue} />}
    <main id="main-content" className="main-content" tabIndex={-1}>
      {isSetup && <div className="setup-brand">SlothBox</div>}
      <Banners status={status.data} error={status.error} checking={status.isFetching}
        disconnected={disconnected} busy={control.isPending}
        onCheck={() => { void status.refetch() }} onResume={() => changeQueue('resume')} />
      <div className="page-heading"><h1 id="page-title" tabIndex={-1}>{title}</h1><Shortcuts /></div>
      {pathname === '/' && status.data?.setupComplete && <Today />}
      {pathname === '/review' && status.data?.setupComplete && <Review />}
      {pathname === '/search' && status.data?.setupComplete && <Search />}
      {pathname === '/receipts' && status.data?.setupComplete && <Receipts />}
      {pathname === '/folders' && status.data?.setupComplete && <Folders />}
      {pathname === '/categories' && status.data?.setupComplete && <Categories />}
      {pathname === '/settings' && status.data?.setupComplete && <Settings />}
      {isSetup && <Setup />}
    </main>
    <FileDrawer />
  </div>
}

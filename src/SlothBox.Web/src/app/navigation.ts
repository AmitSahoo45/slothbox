import { CalendarDays, CircleHelp, Search, ReceiptText, Folder, Tags, Settings } from 'lucide-react'

export const navigation = [
  { path: '/', label: 'Today', icon: CalendarDays },
  { path: '/review', label: 'Needs review', icon: CircleHelp },
  { path: '/search', label: 'Search', icon: Search },
  { path: '/receipts', label: 'Receipts', icon: ReceiptText },
  { path: '/folders', label: 'Folders', icon: Folder },
  { path: '/categories', label: 'Categories', icon: Tags },
  { path: '/settings', label: 'Settings', icon: Settings },
]

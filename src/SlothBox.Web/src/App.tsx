import { useState } from 'react'
import { BrowserRouter } from 'react-router-dom'
import { QueryClientProvider } from '@tanstack/react-query'
import { createQueryClient } from './api/queries'
import { ToastProvider } from './components/Toast'
import { Layout } from './app/Layout'

export function App() {
  const [client] = useState(createQueryClient)
  return <QueryClientProvider client={client}>
    <ToastProvider><BrowserRouter><Layout /></BrowserRouter></ToastProvider>
  </QueryClientProvider>
}

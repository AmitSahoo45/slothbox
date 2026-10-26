import type { Schema } from '../api/types'

export const typeNames: Record<Schema['FileKind'], string> = {
  receipt: 'Receipt', screenshot: 'Screenshot', document: 'Document', other: 'Other', unknown: 'Unknown',
}
export const typeFolders: Record<Schema['FileKind'], string> = {
  receipt: 'Receipts', screenshot: 'Screenshots', document: 'Documents', other: 'Other', unknown: 'Unknown',
}
export const statusNames: Record<Schema['FileStatus'], string> = {
  filed: 'Filed', review: 'Needs review', duplicate: 'Duplicate', undone: 'Undone',
}
export const paymentNames: Record<Schema['PaymentMethod'], string> = {
  upi: 'UPI', card: 'Card', cash: 'Cash', netbanking: 'Net banking', unknown: 'Unknown',
}
export function estimatedTime(value: number | string) {
  const minutes = Math.floor(Number(value))
  if (minutes < 1) return 'under a minute'
  const hours = Math.floor(minutes / 60)
  const remainder = minutes % 60
  return [
    hours ? `${hours} ${hours === 1 ? 'hour' : 'hours'}` : '',
    remainder ? `${remainder} ${remainder === 1 ? 'minute' : 'minutes'}` : '',
  ].filter(Boolean).join(' ')
}
export function localDate(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}
export function displayDate(value: string) {
  return new Date(`${value}T12:00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
}
export function displayTime(value: string) {
  return new Date(value).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false })
}
export function money(amount: number | string, currency: string) {
  try { return new Intl.NumberFormat('en-IN', { style: 'currency', currency }).format(Number(amount)) }
  catch { return `${Number(amount).toLocaleString('en-IN')} ${currency}` }
}
export function relativeTime(at: string) {
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(at).getTime()) / 60000))
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes} min ago`
  if (minutes < 1440) return `${Math.floor(minutes / 60)} h ago`
  const days = Math.floor(minutes / 1440)
  return `${days} ${days === 1 ? 'day' : 'days'} ago`
}
export function originLabel(file: Schema['ActivityItemDto']) {
  return file.folderLabel || (file.origin === 'phone' ? 'phone' : 'watched folder')
}
// GET /phone has no link while the page is off or the laptop has no private Wi-Fi address (spec 7.9).
export function phoneUnavailable(phone: Schema['PhoneDto']) {
  return phone.enabled ? 'No Wi-Fi address found. Connect this laptop to Wi-Fi to use the phone page.'
    : 'The phone page is switched off.'
}

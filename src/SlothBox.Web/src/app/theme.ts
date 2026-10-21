import { createContext, useContext } from 'react'

export type Theme = 'system' | 'light' | 'dark'
export const themeKey = 'slothbox-theme'
export const ThemeContext = createContext<{ theme: Theme; choose: (theme: Theme) => void } | null>(null)
export function readTheme(): Theme {
  try {
    const value = localStorage.getItem(themeKey)
    if (value === 'light' || value === 'dark') return value
  } catch { /* The system preference still works when storage is unavailable. */ }
  return 'system'
}
export function useTheme() {
  const value = useContext(ThemeContext)
  if (!value) throw new Error('Theme provider is missing.')
  return value
}

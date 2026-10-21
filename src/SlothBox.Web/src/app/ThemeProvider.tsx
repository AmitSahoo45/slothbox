import { useEffect, useState, type ReactNode } from 'react'
import { readTheme, ThemeContext, themeKey, type Theme } from './theme'

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setTheme] = useState(readTheme)
  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const apply = () => {
      const dark = theme === 'dark' || (theme === 'system' && media.matches)
      document.documentElement.classList.toggle('dark', dark)
      document.documentElement.style.colorScheme = dark ? 'dark' : 'light'
    }
    apply()
    media.addEventListener('change', apply)
    return () => media.removeEventListener('change', apply)
  }, [theme])
  const choose = (value: Theme) => {
    setTheme(value)
    try { localStorage.setItem(themeKey, value) } catch { /* Keep the choice for this page session. */ }
  }
  return <ThemeContext value={{ theme, choose }}>{children}</ThemeContext>
}

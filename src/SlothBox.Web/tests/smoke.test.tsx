import { render, screen } from '@testing-library/react'
import { expect, test } from 'vitest'
import { App } from '../src/App'

test('renders an accessible application entry point', () => {
  render(<App />)
  expect(screen.getByRole('main')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'SlothBox', level: 1 })).toBeVisible()
})

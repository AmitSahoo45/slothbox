import * as Menu from '@radix-ui/react-dropdown-menu'
import type { Schema } from '../api/types'
import { Button } from './Button'

export function CategoryChoices({ categories, value, onChange, disabled }: {
  categories: Schema['CategoryDto'][]; value: string; onChange: (id: string) => void; disabled?: boolean
}) {
  const extra = categories.slice(9)
  const extraSelected = extra.find(category => String(category.id) === value)
  return <fieldset className="category-choices" disabled={disabled}>
    <legend>Category</legend>
    <div className="category-buttons">{categories.slice(0, 9).map((category, index) =>
      <Button key={category.id} aria-pressed={String(category.id) === value}
        onClick={() => onChange(String(category.id))}>{index + 1} {category.name}</Button>)}
    {extra.length > 0 && <Menu.Root>
      <Menu.Trigger asChild><Button aria-label="More categories" aria-pressed={!!extraSelected}>
        {extraSelected?.name ?? 'More'}
      </Button></Menu.Trigger>
      <Menu.Portal><Menu.Content className="menu" sideOffset={8}>
        <Menu.RadioGroup value={value} onValueChange={onChange}>
          {extra.map(category => <Menu.RadioItem className="menu-item" key={category.id}
            value={String(category.id)}>{category.name}</Menu.RadioItem>)}
        </Menu.RadioGroup>
      </Menu.Content></Menu.Portal>
    </Menu.Root>}</div>
    {!categories.length && <p className="muted">No categories are available for this type.</p>}
  </fieldset>
}

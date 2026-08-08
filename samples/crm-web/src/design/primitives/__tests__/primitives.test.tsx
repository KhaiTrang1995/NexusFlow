import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Button, ButtonGroup } from '../Button'
import { DataTable } from '../DataTable'
import { Meter } from '../Meter'
import { Panel } from '../Panel'
import { Tabs } from '../Tabs'
import { TextField } from '../Field'

/**
 * The primitives' behaviour, not their appearance.
 *
 * WHAT IS ASSERTED HERE IS WHAT A SNAPSHOT CANNOT SEE. Every test below covers a way this design
 * system could look right and be unusable: a whole card that navigates but is not a button, a tab
 * strip that only responds to a mouse, a hint that is rendered but not announced, a meter that
 * overflows its track. Those are the failures that ship, because they are invisible to the person
 * building them.
 */

describe('Button', () => {
  it('is a button and not a submit, so it cannot post a form by accident', () => {
    render(<Button>Save view</Button>)
    expect(screen.getByRole('button', { name: 'Save view' })).toHaveAttribute('type', 'button')
  })

  it('lets a caller ask for a submit explicitly', () => {
    render(<Button type="submit">Send</Button>)
    expect(screen.getByRole('button', { name: 'Send' })).toHaveAttribute('type', 'submit')
  })

  it('warns when an icon-only button has no accessible name', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    render(<Button iconOnly>✕</Button>)
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('aria-label'))
    warn.mockRestore()
  })
})

describe('ButtonGroup', () => {
  it('announces which option is chosen', () => {
    render(
      <ButtonGroup label="Period">
        <Button aria-pressed>Q3</Button>
        <Button aria-pressed={false}>Q2</Button>
      </ButtonGroup>,
    )

    // The same attribute both styles the chosen one and announces it, so the two cannot disagree.
    expect(screen.getByRole('button', { name: 'Q3' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('group', { name: 'Period' })).toBeInTheDocument()
  })
})

describe('Panel', () => {
  it('becomes a real button when it is activatable', async () => {
    const onActivate = vi.fn()
    render(<Panel onActivate={onActivate}>Open pipeline</Panel>)

    const panel = screen.getByRole('button', { name: /Open pipeline/ })
    await userEvent.tab()
    await userEvent.keyboard('{Enter}')

    expect(panel).toHaveFocus()
    expect(onActivate).toHaveBeenCalledOnce()
  })

  it('is a plain section when it is not', () => {
    render(<Panel>Just content</Panel>)
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})

describe('Tabs', () => {
  it('moves between tabs with the arrow keys', async () => {
    const onSelect = vi.fn()
    render(
      <Tabs
        label="Record sections"
        selected="details"
        onSelect={onSelect}
        items={[
          { id: 'details', label: 'Details' },
          { id: 'related', label: 'Related' },
          { id: 'files', label: 'Files' },
        ]}
      />,
    )

    screen.getByRole('tab', { name: 'Details' }).focus()
    await userEvent.keyboard('{ArrowRight}')
    expect(onSelect).toHaveBeenCalledWith('related')

    await userEvent.keyboard('{ArrowLeft}')
    expect(onSelect).toHaveBeenLastCalledWith('files')
  })

  it('shows a count only when there is one', () => {
    render(
      <Tabs
        label="Sections"
        selected="a"
        onSelect={() => {}}
        items={[
          { id: 'a', label: 'With', badge: 3 },
          { id: 'b', label: 'Without', badge: 0 },
        ]}
      />,
    )

    expect(screen.getByRole('tab', { name: /With\s*3/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Without' })).toBeInTheDocument()
  })
})

describe('TextField', () => {
  it('wires the hint to the control so it is announced, not just rendered', () => {
    render(<TextField label="Name" hint="Lower case, letters and underscores." />)

    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveAccessibleDescription(
      'Lower case, letters and underscores.',
    )
  })

  it('announces the refusal and marks the control invalid', () => {
    render(<TextField label="Resolution" error="Cannot be sooner than the response." />)

    const input = screen.getByRole('textbox', { name: 'Resolution' })
    expect(input).toBeInvalid()
    expect(input).toHaveAccessibleDescription('Cannot be sooner than the response.')
  })
})

describe('Meter', () => {
  it('does not overflow its track when the number is over target', () => {
    const { container } = render(<Meter label="Attainment" value={140} target={100} />)

    const fill = container.querySelector('[style*="width"]')
    expect(fill).toHaveStyle({ width: '100%' })
    expect(screen.getByRole('meter', { name: 'Attainment' })).toHaveAttribute('aria-valuenow', '140')
  })
})

describe('DataTable', () => {
  interface Row {
    id: string
    name: string
    amount: number
  }

  const rows: Row[] = [
    { id: '1', name: 'Baltic', amount: 128_000 },
    { id: '2', name: 'Cardinal', amount: 415_000 },
    { id: '3', name: 'Northwind', amount: 184_000 },
  ]

  const columns = [
    { id: 'name', header: 'Account', cell: (row: Row) => row.name, sortValue: (row: Row) => row.name },
    {
      id: 'amount',
      header: 'Amount',
      numeric: true,
      cell: (row: Row) => String(row.amount),
      sortValue: (row: Row) => row.amount,
    },
    { id: 'note', header: 'Note', cell: () => '—' },
  ]

  it('sorts only the columns that said how', async () => {
    render(<DataTable caption="Accounts" columns={columns} rows={rows} rowKey={(row) => row.id} />)

    // A column with no sortValue is not offered as sortable — the honest answer for a column of
    // rendered markup, rather than sorting by whatever String(cell) happens to produce.
    expect(screen.getByRole('button', { name: /Amount/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Note/ })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Amount/ }))

    const cells = screen.getAllByRole('cell').filter((cell) => /^\d+$/.test(cell.textContent ?? ''))
    expect(cells.map((cell) => cell.textContent)).toEqual(['128000', '184000', '415000'])
  })

  it('reverses on a second click and says which way it is sorted', async () => {
    render(<DataTable caption="Accounts" columns={columns} rows={rows} rowKey={(row) => row.id} />)

    const header = screen.getByRole('button', { name: /Amount/ })
    await userEvent.click(header)
    expect(header.closest('th')).toHaveAttribute('aria-sort', 'ascending')

    await userEvent.click(header)
    expect(header.closest('th')).toHaveAttribute('aria-sort', 'descending')
  })

  it('says what would be here, and keeps the header that says what that is', () => {
    render(
      <DataTable
        caption="Accounts"
        columns={columns}
        rows={[]}
        rowKey={(row) => row.id}
        empty="Nothing in this filter is late."
      />,
    )

    expect(screen.getByText('Nothing in this filter is late.')).toBeInTheDocument()

    // The sentence used to replace the table. It replaced the caption and the column headers with
    // it — the reader lost the one thing on the panel that says what would have been here, and a
    // screen reader lost the table the sentence belongs to.
    expect(screen.getByRole('table', { name: 'Accounts' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Account/ })).toBeInTheDocument()

    // And it is still empty: the message is not a row, so nothing counting rows counts it.
    expect(screen.queryAllByRole('row', { name: /Baltic/ })).toHaveLength(0)
    expect(document.querySelectorAll('tbody tr')).toHaveLength(0)
  })

  it('still says it is empty when the caller gave no sentence to say', () => {
    // A header row over blank space is what a half-drawn panel looks like. The default is a poor
    // sentence, which is the caller's fault; no sentence at all was the table's.
    render(<DataTable caption="Accounts" columns={columns} rows={[]} rowKey={(row) => row.id} />)

    expect(screen.getByText('No rows.')).toBeInTheDocument()
  })

  it('orders text the way it is read, not by code unit', async () => {
    const mixed: Row[] = [
      { id: '1', name: 'Zenith', amount: 1 },
      { id: '2', name: 'acme', amount: 2 },
      { id: '3', name: 'Region 10', amount: 3 },
      { id: '4', name: 'Region 9', amount: 4 },
    ]

    render(<DataTable caption="Accounts" columns={columns} rows={mixed} rowKey={(row) => row.id} />)

    await userEvent.click(screen.getByRole('button', { name: /Account/ }))

    // `<` compares UTF-16 code units: it puts every lower-case name after every upper-case one and
    // "Region 10" before "Region 9". A column that says it is sorted and is not is worse than one
    // that never offered to sort.
    const names = screen
      .getAllByRole('cell')
      .filter((cell) => /^(Zenith|acme|Region \d+)$/.test(cell.textContent ?? ''))
      .map((cell) => cell.textContent)

    expect(names).toEqual(['acme', 'Region 9', 'Region 10', 'Zenith'])
  })
})

describe('TextField, label hidden', () => {
  it('keeps the name for a screen reader when the column header carries it on screen', () => {
    // A control in a table cell: the header names it visually, so repeating the label under every
    // input is noise. Removing it outright would leave the input nameless to anybody not looking
    // at the header, which is the trade this prop exists to refuse.
    render(<TextField label="Quantity" hideLabel defaultValue="120" />)

    const input = screen.getByRole('textbox', { name: 'Quantity' })
    expect(input).toBeInTheDocument()
    expect(screen.getByText('Quantity')).toHaveClass('sr-only')
  })
})

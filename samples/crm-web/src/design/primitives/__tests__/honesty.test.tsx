import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Funnel, ShareBar, Sparkline, StackedBars, Waterfall, scaleTo } from '@/design/charts'
import { Drawer } from '../Drawer'
import { SelectField } from '../Field'
import { Meter } from '../Meter'
import { Skeleton } from '../States'
import { StatStrip } from '../StatTile'
import { Tabs } from '../Tabs'
import { Tag } from '../Tag'

/**
 * What a primitive draws when it was given nothing, or given something nobody meant.
 *
 * A FAULT IN A PRIMITIVE IS THE SAME FAULT ON TWENTY SCREENS. Every case below was a way for one
 * of these components to state a fact it had not been told: a meter reading nought per cent
 * against a quota that does not exist, a funnel with a block over every empty stage, a share bar
 * announcing "Won 0%, Lost 0%" for a quarter in which nothing has been decided, a bar chart whose
 * hidden table puts each number under the wrong heading. None of them looks wrong on the screen,
 * which is exactly why they survived.
 */

describe('Meter, against a target that is not there', () => {
  it('draws no proportion when the target is zero', () => {
    render(<Meter label="Quota attainment" value={48_000} target={0} />)

    // An empty track is a statement about the seller. A quota of zero is a statement about the
    // plan, and drawing the second as the first is how "achieved nothing" gets onto a review.
    expect(screen.queryByRole('meter')).not.toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Quota attainment: no target set' })).toBeInTheDocument()
  })

  it('draws no proportion when the reading is not a number', () => {
    const { container } = render(<Meter label="Coverage" value={Number.NaN} target={100} />)

    expect(screen.getByRole('img', { name: 'Coverage: no reading' })).toBeInTheDocument()

    // `width: NaN%` is dropped by the browser, and a fill with no width of its own is a full bar.
    expect(container.querySelector('[style*="width"]')).toBeNull()
  })

  it('still measures a real number against a real target', () => {
    render(<Meter label="Attainment" value={62} target={100} />)

    expect(screen.getByRole('meter', { name: 'Attainment' })).toHaveAttribute('aria-valuenow', '62')
  })
})

describe('Skeleton', () => {
  it('says it is loading to a reader who cannot see the bars', () => {
    // The whole placeholder was aria-hidden, so a panel mid-read and a panel that came back empty
    // were the same thing — silence — to anybody not looking at it.
    render(<Skeleton rows={2} />)

    expect(screen.getByText('Loading…')).toHaveClass('sr-only')
  })

  it('keeps every bar inside its track however many there are', () => {
    const { container } = render(<Skeleton rows={20} />)

    const widths = [...container.querySelectorAll<HTMLElement>('[style*="width"]')].map(
      (bar) => Number.parseFloat(bar.style.width),
    )

    // Past fourteen rows the taper went negative, the declaration was dropped, and the bar drew
    // full width — a stack that widens as it goes, which reads as content rather than as a wait.
    expect(widths).toHaveLength(20)
    expect(Math.min(...widths)).toBeGreaterThan(0)
    expect(Math.max(...widths)).toBeLessThanOrEqual(100)
  })
})

describe('scaleTo', () => {
  it('draws nothing for nothing, and keeps a small value visible', () => {
    const scale = scaleTo([0, 5, 100], 200, 18)

    // The floor exists so a small value is still on the chart. Applied to a zero it invents one.
    expect(scale(0)).toBe(0)
    expect(scale(5)).toBe(18)
    expect(scale(100)).toBe(200)
  })

  it('draws nothing for a value that is not a number', () => {
    expect(scaleTo([10, Number.NaN], 100)(Number.NaN)).toBe(0)
  })
})

describe('StackedBars', () => {
  const series = [
    { label: 'Open', colour: 'var(--color-accent)' },
    { label: 'Won', colour: 'var(--color-positive)' },
    { label: 'Lost', colour: 'var(--color-neutral-500)' },
  ]

  it('says which chart came back empty rather than drawing an empty frame', () => {
    render(<StackedBars caption="Open, won and lost value" series={series} bars={[]} />)

    expect(screen.getByText('Open, won and lost value: nothing to draw.')).toBeInTheDocument()
  })

  it('keeps a short row under the right headings in the hidden table', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})

    render(
      <StackedBars
        caption="Value by quarter"
        series={series}
        bars={[{ label: 'Q3', values: [40, 12], readout: '$52k' }]}
      />,
    )

    // Two values for three series. The row used to be two cells long, so "12" sat under "Won"
    // and the Lost column simply ended — the numbers a screen reader gets were not the picture's.
    const cells = within(screen.getByRole('table', { name: 'Value by quarter' }))
      .getAllByRole('cell')
      .map((cell) => cell.textContent)

    expect(cells).toEqual(['Q3', '40', '12', '—', '$52k'])
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('3 series'))
    warn.mockRestore()
  })

  it('gives the hidden table the total the picture shouts', () => {
    render(
      <StackedBars
        caption="Pipeline"
        series={[{ label: 'Open', colour: 'var(--color-accent)' }]}
        bars={[{ label: 'Q3', values: [40], readout: '$40k' }]}
      />,
    )

    const headers = within(screen.getByRole('table', { name: 'Pipeline' }))
      .getAllByRole('columnheader')
      .map((header) => header.textContent)

    expect(headers).toEqual(['', 'Open', 'Total'])
    expect(screen.getByRole('cell', { name: '$40k' })).toBeInTheDocument()
  })
})

describe('Waterfall', () => {
  it('says which chart came back empty', () => {
    render(<Waterfall caption="Where the balance went" steps={[]} />)

    expect(screen.getByText('Where the balance went: nothing to draw.')).toBeInTheDocument()
  })
})

describe('Funnel', () => {
  const stages = [
    { name: 'Discovery', value: 120_000, amount: '$120k', meta: '4 deals' },
    { name: 'Proposal', value: 0, amount: '$0', meta: '0 deals' },
  ]

  it('gives a keyboard the same funnel a mouse gets', async () => {
    const onSelect = vi.fn()
    render(<Funnel caption="Open pipeline by stage" stages={stages} onSelect={onSelect} />)

    // It was a div with an onClick and a pointer cursor: live to a mouse, and to nothing else.
    const block = screen.getByRole('button', { name: 'Discovery: $120k' })
    block.focus()
    await userEvent.keyboard('{Enter}')

    expect(onSelect).toHaveBeenCalledWith('Discovery')
  })

  it('draws no block over a stage with nothing in it', () => {
    render(<Funnel caption="Open pipeline by stage" stages={stages} onSelect={() => {}} />)

    // The floor under the scale gave every empty stage the same eighteen pixels as a stage with
    // real money in it, so an empty pipeline was drawn as a working one.
    expect(screen.queryByRole('button', { name: 'Proposal: $0' })).not.toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Proposal' })).toBeInTheDocument()
  })

  it('says which chart came back empty', () => {
    render(<Funnel caption="Open pipeline by stage" stages={[]} />)

    expect(screen.getByText('Open pipeline by stage: nothing to draw.')).toBeInTheDocument()
  })
})

describe('Sparkline', () => {
  it('says it has no readings rather than drawing an empty box', () => {
    const { container } = render(<Sparkline caption="Weekly bookings" values={[]} />)

    expect(container.querySelector('title')?.textContent).toBe('Weekly bookings: no readings')
    expect(container.querySelector('polyline')).toBeNull()
  })

  it('draws one reading as a point, not as an invisible line', () => {
    const { container } = render(<Sparkline caption="Weekly bookings" values={[42]} />)

    // A polyline with one point draws nothing at all, so a series of one looked like a series of
    // none. Two coincident points and a round cap is a dot, which is what one reading is.
    expect(container.querySelector('title')?.textContent).toBe('Weekly bookings: one reading')
    expect(container.querySelector('polyline')?.getAttribute('points')).toBe('50.00,0.00 50.00,0.00')
  })

  it('drops a reading that is not a number instead of voiding the whole line', () => {
    const { container } = render(<Sparkline caption="Weekly bookings" values={[10, Number.NaN, 30]} />)

    const points = container.querySelector('polyline')?.getAttribute('points') ?? ''

    expect(points).not.toContain('NaN')
    expect(points.split(' ')).toHaveLength(2)
  })
})

describe('ShareBar', () => {
  it('has no shares when there is no total', () => {
    const { container } = render(
      <ShareBar
        caption="Won against lost"
        parts={[
          { label: 'Won', value: 0, colour: 'var(--color-positive)' },
          { label: 'Lost', value: 0, colour: 'var(--color-neutral-500)' },
        ]}
      />,
    )

    // `total || 1` turned an empty total into a real one and announced "Won 0%, Lost 0%" — a
    // measurement of a quarter in which nothing has been decided.
    expect(screen.getByRole('img', { name: 'Won against lost: nothing recorded' })).toBeInTheDocument()
    expect(container.querySelectorAll('[title]')).toHaveLength(0)
  })

  it('leaves a negative out of the divisor rather than pushing the rest past the end', () => {
    render(
      <ShareBar
        caption="Credit by campaign"
        parts={[
          { label: 'Spring', value: 75, colour: 'var(--color-accent)' },
          { label: 'Autumn', value: 25, colour: 'var(--color-accent-400)' },
          { label: 'Adjustment', value: -50, colour: 'var(--color-critical)' },
        ]}
      />,
    )

    // Summed in, the divisor is 50 and Spring is drawn at 150% of a track it cannot leave.
    expect(
      screen.getByRole('img', { name: 'Credit by campaign: Spring 75%, Autumn 25%' }),
    ).toBeInTheDocument()
  })
})

describe('StatStrip', () => {
  it('draws no bar for a fraction that is not a number', () => {
    const { container } = render(
      <StatStrip cells={[{ label: 'Weighted', value: '$0', fraction: 0 / 0 }]} />,
    )

    // `width: NaN%` is dropped and the fill takes its track: a ratio over an empty denominator
    // came out as a full bar, which is the one reading it certainly did not mean. Asserted on the
    // track rather than on the width, because the invalid width is exactly what does not survive
    // into the attribute — the bar is there, at its full size, and looks deliberate.
    expect(container.querySelectorAll('[style*="border-radius"]')).toHaveLength(0)
  })

  it('still draws a real fraction', () => {
    const { container } = render(
      <StatStrip cells={[{ label: 'Weighted', value: '$40k', fraction: 0.4 }]} />,
    )

    expect(container.querySelector('[style*="width"]')).toHaveStyle({ width: '40%' })
  })
})

describe('Tabs', () => {
  it('stays reachable by keyboard when the selection matches no tab', async () => {
    const onSelect = vi.fn()
    render(
      <Tabs
        label="Record sections"
        selected="archived"
        onSelect={onSelect}
        items={[
          { id: 'details', label: 'Details' },
          { id: 'related', label: 'Related' },
        ]}
      />,
    )

    // Every tab was tabIndex -1, so the strip had no tab stop at all: a control that is plainly
    // there, plainly interactive, and cannot be reached without a mouse.
    await userEvent.tab()

    expect(screen.getByRole('tab', { name: 'Details' })).toHaveFocus()
  })
})

describe('Drawer', () => {
  it('is named even when its heading is not a plain string', () => {
    // `aria-label` was set only when the title happened to be a string. The usual heading here is
    // a name beside a tag, so the peek opened as a dialog with no name.
    render(
      <Drawer
        title={
          <>
            Northwind <Tag tone="accent">Gold</Tag>
          </>
        }
        onClose={() => {}}
      >
        Body
      </Drawer>,
    )

    expect(screen.getByRole('dialog', { name: /Northwind/ })).toBeInTheDocument()
  })

  it('keeps Tab inside the panel it has declared modal', async () => {
    render(
      <Drawer title="Northwind" onClose={() => {}}>
        <button type="button">Edit</button>
        <button type="button">Delete</button>
      </Drawer>,
    )

    screen.getByRole('button', { name: 'Delete' }).focus()
    await userEvent.tab()

    // `aria-modal` hides everything behind the panel from a screen reader. Tab went through into
    // it anyway, so the next stop after the last button was a row that had been announced as
    // nothing — present, clickable, and unreadable.
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus()
  })
})

describe('SelectField', () => {
  it('shows a held value the list no longer offers', () => {
    render(
      <SelectField
        label="Owner"
        value="r.halloran"
        onChange={() => {}}
        options={[
          { value: 'a.ruiz', label: 'A. Ruiz' },
          { value: 'j.tan', label: 'J. Tan' },
        ]}
      />,
    )

    // A select whose value matches no option draws nothing, and blank reads as "not set": the
    // reader picks something, and the value the record actually held is gone without a word.
    const select = screen.getByRole('combobox', { name: 'Owner' })

    expect(select).toHaveValue('r.halloran')
    expect(screen.getByRole('option', { name: 'r.halloran' })).toBeDisabled()
  })

  it('adds nothing when the value is one of the options', () => {
    render(
      <SelectField
        label="Owner"
        value="j.tan"
        onChange={() => {}}
        options={[
          { value: 'a.ruiz', label: 'A. Ruiz' },
          { value: 'j.tan', label: 'J. Tan' },
        ]}
      />,
    )

    expect(screen.getAllByRole('option')).toHaveLength(2)
  })
})

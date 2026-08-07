import { useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { Button, ButtonGroup, Page, PageHeader, Tag } from '@/design/primitives'
import { useToast } from '@/app/ToastProvider'
import { fullMoney, money } from '@/lib/format'
import { useEntityPage } from '@/api/queries/hooks'
import { modelFor } from '@/fixtures/objects'
import { toRows } from './liveRecords'
import type { RecordRow } from '@/fixtures/objects'
import styles from './KanbanScreen.module.css'

/**
 * The opportunity board.
 *
 * DRAG IS NOT THE ONLY WAY TO MOVE A CARD. Every card also has a keyboard path — focus it and use
 * the left and right arrows — because a board whose single interaction is a mouse gesture is a
 * board a whole class of people cannot use at all. The two paths call the same `move`, so they
 * cannot drift.
 *
 * THE MOVE IS LOCAL AND SAYS SO. The .NET sample advances an opportunity through
 * `/opportunities/triggers`, which announces an intent and lets the configured process decide —
 * it does not take "put this deal in that column". So the board moves the card here and the toast
 * says the move is not yet written; inventing an endpoint that does what the backend deliberately
 * refuses to do would be worse than being honest about it.
 */
export function KanbanScreen() {
  const navigate = useNavigate()
  const toast = useToast()
  const model = modelFor('opportunity')

  const [moved, setMoved] = useState<Readonly<Record<string, string>>>({})
  const [dragging, setDragging] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)
  const [scope, setScope] = useState<'mine' | 'all'>('all')

  const stages = useMemo(
    () => (model.stages ?? []).filter((stage) => !stage.lost),
    [model.stages],
  )

  const stageOf = (row: RecordRow) => moved[row.id] ?? String(row['stage'])

  // Live opportunities, grouped by the stage the configured process put them in. The stage is a
  // column of the answer without being a column of the table — the server merges its name into
  // the projection, because an identifier is not something a board can group by.
  const page = useEntityPage('Opportunity')
  const live = page.data !== undefined

  const source = useMemo(
    () => (live ? toRows('opportunity', model, page.data!.records) : model.records),
    [live, model, page.data],
  )

  const rows = useMemo(
    () => source.filter((row) => scope === 'all' || row['owner'] === 'A. Ruiz'),
    [source, scope],
  )

  function move(row: RecordRow, direction: -1 | 1) {
    const index = stages.findIndex((stage) => stage.name === stageOf(row))
    const next = stages[index + direction]
    if (!next) return
    setMoved((current) => ({ ...current, [row.id]: next.name }))
    toast.saved(`${row['name']} → ${next.name} (not written; the process decides)`)
  }

  function drop(stageName: string) {
    const row = rows.find((candidate) => candidate.id === dragging)
    setDragging(null)
    setOver(null)
    if (!row || stageOf(row) === stageName) return
    setMoved((current) => ({ ...current, [row.id]: stageName }))
    toast.saved(`${row['name']} → ${stageName} (not written; the process decides)`)
  }

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow="Opportunities · board"
        title="Pipeline board"
        actions={
          <>
            <ButtonGroup label="Whose deals">
              <Button size="sm" aria-pressed={scope === 'mine'} onClick={() => setScope('mine')}>
                Mine
              </Button>
              <Button size="sm" aria-pressed={scope === 'all'} onClick={() => setScope('all')}>
                Everyone
              </Button>
            </ButtonGroup>
            <Button onClick={() => void navigate({ to: '/records/$object', params: { object: 'opportunity' } })}>
              List
            </Button>
          </>
        }
      />

      <div className={styles.board}>
        {stages.map((stage) => {
          const cards = rows.filter((row) => stageOf(row) === stage.name)
          const sum = cards.reduce((total, row) => total + Number(row['amount'] ?? 0), 0)

          return (
            <section
              key={stage.name}
              className={`${styles.column} ${over === stage.name ? styles.columnOver : ''}`}
              onDragOver={(event) => {
                event.preventDefault()
                setOver(stage.name)
              }}
              onDragLeave={() => setOver((current) => (current === stage.name ? null : current))}
              onDrop={() => drop(stage.name)}
            >
              <header className={styles.columnHead}>
                <div className={styles.columnName}>
                  {stage.name}
                  <span className={styles.columnCount}>{cards.length}</span>
                </div>
                <div className={styles.columnSum}>
                  {money(sum)} · {stage.pct}%
                </div>
              </header>

              <div className={styles.cards}>
                {cards.map((row) => (
                  <button
                    key={row.id}
                    type="button"
                    draggable
                    className={`${styles.card} ${dragging === row.id ? styles.cardDragging : ''}`}
                    onDragStart={() => setDragging(row.id)}
                    onDragEnd={() => setDragging(null)}
                    onClick={() =>
                      void navigate({
                        to: '/records/$object/$id',
                        params: { object: 'opportunity', id: row.id },
                      })
                    }
                    onKeyDown={(event) => {
                      if (event.key === 'ArrowRight') {
                        event.preventDefault()
                        move(row, 1)
                      }
                      if (event.key === 'ArrowLeft') {
                        event.preventDefault()
                        move(row, -1)
                      }
                    }}
                    aria-label={`${row['name']}, ${stageOf(row)}. Left and right arrows move it between stages.`}
                  >
                    <div className={styles.cardName}>{row['name']}</div>
                    <div className={styles.cardMeta}>
                      <Tag tone="outline">{row['owner']}</Tag>
                      <span>{row['closeDate']}</span>
                      <span className={styles.cardAmount}>{fullMoney(Number(row['amount']))}</span>
                    </div>
                  </button>
                ))}
              </div>
            </section>
          )
        })}
      </div>
    </Page>
  )
}

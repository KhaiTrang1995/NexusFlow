import { useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { Button, ButtonGroup, EmptyState, Page, PageHeader, Skeleton, Tag } from '@/design/primitives'
import { useToast } from '@/app/ToastProvider'
import { useSession } from '@/session/SessionProvider'
import { date, fullMoney, money } from '@/lib/format'
import { useAdvanceOpportunity, useEntityPage, useProcess } from '@/api/queries/hooks'
import type { ProcessTransitionView, RecordView } from '@/api/contracts'
import styles from './KanbanScreen.module.css'

/**
 * The opportunity board, over the published process.
 *
 * THE LANES WERE THE PROTOTYPE'S. They came from the object model — a stage list this client was
 * compiled with, each carrying an invented probability drawn as "· 30%" under the column total.
 * They matched the seeded process by coincidence of naming; on a tenant that named its stages
 * anything else, every card would have fallen into no lane at all and the board would have looked
 * empty rather than wrong.
 *
 * THE MOVE IS WRITTEN NOW, AND IT IS THE PROCESS THAT WRITES IT. Dropping a card used to move it
 * locally and toast "not written; the process decides" — true at the time, because nothing here
 * could name a trigger. The published transitions carry one: a drop from Discovery to Qualify is
 * whichever trigger the administrator declared between those two stages, and the server decides
 * whether its guards hold. A lane with no transition into it from where the card is refuses the
 * drop, because that is what the process says.
 *
 * DRAG IS NOT THE ONLY WAY TO MOVE A CARD. Every card also has a keyboard path — focus it and use
 * the left and right arrows — because a board whose single interaction is a mouse gesture is a
 * board a whole class of people cannot use at all. Both paths call the same `advance`, so they
 * cannot drift.
 */
export function KanbanScreen() {
  const navigate = useNavigate()
  const toast = useToast()
  const { ownerId } = useSession()

  const process = useProcess('Opportunity')
  const page = useEntityPage('Opportunity')
  const advance = useAdvanceOpportunity()

  const [dragging, setDragging] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)
  const [scope, setScope] = useState<'mine' | 'all'>('all')

  // Every stage the process declares, in its own order. The lost one is left out because a board
  // is for work in progress; a lane nothing is meant to come back from is a filter, not a column.
  const stages = useMemo(
    () => (process.data?.stages ?? []).filter((stage) => !stage.name.toLowerCase().includes('lost')),
    [process.data],
  )

  const rows = useMemo(
    () =>
      (page.data?.records ?? []).filter(
        (row) => scope === 'all' || row.values['owner_id'] === ownerId,
      ),
    [page.data, scope, ownerId],
  )

  if (process.isPending || page.isPending) {
    return (
      <Page layout="full">
        <Skeleton rows={8} />
      </Page>
    )
  }

  if (process.isError) {
    return (
      <Page layout="full">
        <EmptyState
          title="No process is published for opportunities"
          detail="A board is the published stages. Publish one and the lanes appear — this build does not invent them."
        />
      </Page>
    )
  }

  /**
   * Moves a card by naming what happened, not where it should land.
   *
   * The transition between the two stages is what carries the trigger. Where there is none the
   * move is refused here rather than sent: the server would refuse it too, and saying so without
   * a round trip is the same answer sooner.
   */
  function advanceTo(row: RecordView, toStage: string) {
    const from = row.values['stage'] ?? ''

    if (from === toStage) {
      return
    }

    const transition = (process.data?.transitions ?? []).find(
      (candidate: ProcessTransitionView) => candidate.from === from && candidate.to === toStage,
    )

    if (transition === undefined) {
      toast.failed(
        new Error(`The published process has no move from ${from} to ${toStage}.`),
        'That move is not in the process.',
      )
      return
    }

    const deal = row.values['name'] ?? 'The deal'

    advance.mutate(
      {
        opportunityId: row.recordId,
        trigger: transition.trigger,

        // NOT YET PROCESSED, SAID AS SOON AS IT IS TRUE. A 200 from the advance means the
        // application is recorded and nobody has decided it, and nothing more. A board that said
        // nothing until the engine answered would leave a drag looking like it had been dropped.
        applied: (application) =>
          toast.saved(`${application.trigger} is with the process, which has not decided yet.`),
      },
      {
        // THE CARD USED TO SPRING BACK UNDER A TOAST SAYING IT HAD MOVED. The trigger is applied
        // synchronously and the transition is decided afterwards, so the board refetched before
        // the engine had run — and when the engine declined the move, nothing ever corrected the
        // toast. Reading the stage back fixed the first case and disguised the second: "the
        // process left this deal in Discovery" is what a decline and a slow feed both look like
        // from here. The three now read differently, and the server is what tells them apart.
        onSuccess: (outcome) => {
          if (outcome.outcome === 'Moved') {
            toast.saved(`${deal} → ${outcome.stage}.`)
            return
          }

          if (outcome.outcome === 'Declined') {
            toast.saved(
              `The process declined ${outcome.trigger} and left ${deal} in ${outcome.stage}`
              + (transition.guards.length > 0 ? ' — check its guards.' : '.'),
            )
            return
          }

          toast.saved(
            `${outcome.trigger} is with the process, which has not decided yet. `
            + `${deal} is still in ${outcome.stage}.`,
          )
        },

        onError: (error) => toast.failed(error, `${transition.trigger} was refused.`),
      },
    )
  }

  /** One lane left or right, through whatever transition connects them. */
  function step(row: RecordView, direction: -1 | 1) {
    const index = stages.findIndex((stage) => stage.name === row.values['stage'])
    const next = stages[index + direction]

    if (next !== undefined) {
      advanceTo(row, next.name)
    }
  }

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow={`Opportunities · board · version ${process.data.version}`}
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
          const cards = rows.filter((row) => row.values['stage'] === stage.name)
          const sum = cards.reduce((total, row) => total + Number(row.values['amount'] ?? 0), 0)

          // The average likelihood of what is actually in the lane. The invented per-stage
          // percentage that used to sit here was the same on every tenant and belonged to no
          // deal; this is a fact about these cards.
          const likelihood =
            cards.length === 0
              ? null
              : Math.round(
                  cards.reduce((total, row) => total + Number(row.values['probability'] ?? 0), 0)
                    / cards.length,
                )

          return (
            <section
              key={stage.name}
              className={`${styles.column} ${over === stage.name ? styles.columnOver : ''}`}
              onDragOver={(event) => {
                event.preventDefault()
                setOver(stage.name)
              }}
              onDragLeave={() => setOver((current) => (current === stage.name ? null : current))}
              onDrop={() => {
                const row = rows.find((candidate) => candidate.recordId === dragging)

                setDragging(null)
                setOver(null)

                if (row !== undefined) {
                  advanceTo(row, stage.name)
                }
              }}
            >
              <header className={styles.columnHead}>
                <div className={styles.columnName}>
                  {stage.name}
                  <span className={styles.columnCount}>{cards.length}</span>
                </div>
                <div className={styles.columnSum}>
                  {money(sum)}
                  {likelihood === null ? '' : ` · ${likelihood}% likely`}
                </div>
              </header>

              <div className={styles.cards}>
                {cards.map((row) => (
                  <button
                    key={row.recordId}
                    type="button"
                    draggable
                    className={`${styles.card} ${dragging === row.recordId ? styles.cardDragging : ''}`}
                    onDragStart={() => setDragging(row.recordId)}
                    onDragEnd={() => setDragging(null)}
                    onClick={() =>
                      void navigate({
                        to: '/records/$object/$id',
                        params: { object: 'opportunity', id: row.recordId },
                      })
                    }
                    onKeyDown={(event) => {
                      if (event.key === 'ArrowRight') {
                        event.preventDefault()
                        step(row, 1)
                      }
                      if (event.key === 'ArrowLeft') {
                        event.preventDefault()
                        step(row, -1)
                      }
                    }}
                    aria-label={`${row.values['name'] ?? 'Opportunity'}, ${stage.name}. Left and right arrows move it between stages.`}
                  >
                    <div className={styles.cardName}>{row.values['name'] ?? '—'}</div>
                    <div className={styles.cardMeta}>
                      <Tag tone="outline">{row.values['probability'] ?? 0}%</Tag>
                      <span>{date(row.values['expected_close'])}</span>
                      <span className={styles.cardAmount}>
                        {fullMoney(Number(row.values['amount'] ?? 0))}
                      </span>
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

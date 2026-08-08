import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  Columns,
  EmptyState,
  ErrorState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
  TextAreaField,
  TextField,
} from '@/design/primitives'
import { usePeriodRollUp, useSetStrategy } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { useToast } from '@/app/ToastProvider'
import { money } from '@/lib/format'
import { usePeriod, withPeriod } from '@/features/exec/period'
import { PeriodPicker } from '@/features/exec/PeriodPicker'
import { PeriodGate, hasNoPeriod } from './PeriodGate'
import styles from './planning.module.css'

/**
 * The number and the vision beside it.
 *
 * THE FORM WROTE NOTHING. It toasted "recorded" and posted nothing at all — on the screen that
 * sets the number every executive surface rolls up to, and without which the board answers
 * `crm.strategy_not_set` rather than a figure.
 *
 * ONE STRATEGY PER PERIOD, AND THE VISION IS NOT DECORATION. The backend allows exactly one, and
 * requires the sentence: a period with a target and no statement of what it is for is a number
 * every level below will interpret differently, which is how four teams end up covering the same
 * quarter four different ways.
 *
 * AND IT WAS OFFERED TO PEOPLE THE SERVER REFUSES. `crm.planning.strategy` requires `crm.admin`;
 * a representative filled the form in, pressed the button and was told they may not. The panel
 * now says who sets this instead — the refusal is the server's either way, but a form nobody
 * clears is a form that teaches people this application's buttons are decorative.
 */
export function StrategyScreen() {
  const toast = useToast()
  const session = useSession()
  const choice = usePeriod()
  const rollUp = usePeriodRollUp(choice.period)
  const set = useSetStrategy()
  const maySet = session.can('crm.admin')

  const [target, setTarget] = useState('')
  const [vision, setVision] = useState('')

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Planning', choice)}
        title="Strategy"
        actions={<PeriodPicker choice={choice} />}
      />

      <PeriodGate choice={choice} what="the strategy" />

      <AsyncBoundary query={rollUp} skeletonRows={4} hidden={hasNoPeriod(choice)}>
        {(data) => (
          <>
            <StatGrid columns={3}>
              <StatTile label="Target" value={money(data.target)} note={data.currency} />
              <StatTile label="Committed" value={money(data.committed)} note="by the levels below" />
              <StatTile
                label="Gap"
                value={money(data.gap)}
                direction={data.gap > 0 ? 'down' : 'up'}
                note="target less committed"
              />
            </StatGrid>

            <Columns layout="split">
              <Panel padding="flush">
                <PanelHeader title="The vision in force" note={choice.label} />
                <p className={styles.vision}>{data.vision}</p>
                <PanelBody style={{ borderTop: '1px solid var(--color-divider)' }}>
                  <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                    <Tag tone="accent">{data.accounts.length} account plans</Tag>
                    <Tag tone="accent">{data.opportunities.length} deal plans</Tag>
                    <Tag tone="accent">{data.marketing.length} demand plans</Tag>
                  </div>
                </PanelBody>
              </Panel>

              <Panel padding="flush">
                <PanelHeader
                  title="Set the number"
                  note={maySet ? 'one per period; a second replaces it' : 'an administrator sets this'}
                />
                {maySet ? (
                  <PanelBody>
                    <form
                      style={{ display: 'grid', gap: 12 }}
                      onSubmit={(event) => {
                        event.preventDefault()

                        set.mutate(
                          {
                            period: choice.period as string,
                            vision: vision.trim(),
                            target: Number(target),

                            // The period's own currency, not one this form asks for. A strategy
                            // in a currency the roll-up beside it does not use is two numbers
                            // that cannot be compared, and nothing would say which was which.
                            currency: data.currency,
                          },
                          {
                            onSuccess: () =>
                              toast.saved(
                                `${choice.label} is set to ${money(Number(target))} ${data.currency}.`,
                              ),
                            onError: (error) => toast.failed(error, 'That strategy was refused.'),
                          },
                        )
                      }}
                    >
                      <TextField
                        label="Target"
                        type="number"
                        required
                        hint={`In ${data.currency}. This is the number every level below rolls up to.`}
                        value={target}
                        onChange={(event) => setTarget(event.target.value)}
                      />
                      <TextAreaField
                        label="Vision"
                        required
                        hint="What the number is for. Required, because a target with no statement is four interpretations."
                        value={vision}
                        onChange={(event) => setVision(event.target.value)}
                      />
                      <Button
                        type="submit"
                        tone="primary"
                        disabled={target.trim() === '' || vision.trim() === '' || set.isPending}
                      >
                        {set.isPending ? 'Setting…' : 'Set the strategy'}
                      </Button>

                      {set.isError ? <ErrorState error={set.error} /> : null}
                    </form>
                  </PanelBody>
                ) : (
                  <EmptyState
                    title="You do not set the number for this period"
                    detail="Setting a strategy needs crm.admin, which this token does not carry. The vision beside this is the one in force; a manager or a director changes it."
                  />
                )}
              </Panel>
            </Columns>
          </>
        )}
      </AsyncBoundary>
    </Page>
  )
}

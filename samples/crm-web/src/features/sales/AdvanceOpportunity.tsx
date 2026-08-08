import { Button, Tag } from '@/design/primitives'
import { useAdvanceOpportunity, useProcess } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { ProcessTransitionView } from '@/api/contracts'

/**
 * The moves available from where this opportunity actually is.
 *
 * THE BUTTONS ARE THE PUBLISHED PROCESS'S TRANSITIONS, NOT A LIST WRITTEN HERE. An administrator
 * adds a transition in setup and a button appears; retires one and the button goes. A screen
 * offering a move the process does not have is a screen whose buttons return errors, and the
 * reader blames the deal rather than the configuration.
 *
 * WHAT IS SENT IS THE TRIGGER, NOT THE DESTINATION. Where it lands is the process's answer, and a
 * client that sent a stage would be deciding it — which is how two implementations of the same
 * pipeline end up disagreeing about what "qualified" means.
 *
 * A TRANSITION WITH GUARDS IS SHOWN, NOT HIDDEN. The guard may not hold for this record, and the
 * server is the one that knows; hiding it makes the move look like it does not exist. The guard
 * is named beside the button so a refusal is expected rather than mysterious.
 */
export function AdvanceOpportunity({
  opportunityId,
  stage,
}: {
  opportunityId: string
  stage: string | null
}) {
  const process = useProcess('Opportunity')
  const advance = useAdvanceOpportunity()
  const toast = useToast()

  const available = (process.data?.transitions ?? []).filter(
    (transition) => transition.from === stage,
  )

  if (process.isPending || available.length === 0) {
    return null
  }

  function apply(transition: ProcessTransitionView) {
    advance.mutate(
      {
        opportunityId,
        trigger: transition.trigger,

        // NOT YET PROCESSED, SAID AS SOON AS IT IS TRUE. All a 200 from the advance means is that
        // the application is recorded and nothing has decided it. Saying that here is what keeps
        // the sentence below from having to cover it.
        applied: (application) =>
          toast.saved(`${application.trigger} is with the process, which has not decided yet.`),
      },
      {
        // WHICH OF THE THREE, IN THE SERVER'S OWN WORDS. This said "Moved to Qualify" on the 200
        // — before the engine had run, and whether or not it then moved anything. Then it said
        // "the process left this deal in Discovery", which covered a declined move and a feed
        // that had not arrived with one sentence that was a fabrication in one of the two cases.
        // Declining is not an error, so neither version had anything to catch. The outcome now
        // comes off the row the engine wrote, and the three answers read differently.
        onSuccess: (outcome) => {
          if (outcome.outcome === 'Moved') {
            toast.saved(`Moved to ${outcome.stage}.`)
            return
          }

          if (outcome.outcome === 'Declined') {
            toast.saved(
              `The process declined ${outcome.trigger} and left this deal in ${outcome.stage}`
              + (transition.guards.length > 0 ? ' — check its guards.' : '.'),
            )
            return
          }

          toast.saved(
            `${outcome.trigger} is with the process, which has not decided yet. `
            + `This deal is still in ${outcome.stage}.`,
          )
        },

        onError: (error) => toast.failed(error, `${transition.trigger} was refused.`),
      },
    )
  }

  return (
    <>
      {available.map((transition) => (
        <Button
          key={transition.trigger}
          disabled={advance.isPending}
          title={describe(transition)}
          onClick={() => apply(transition)}
        >
          {transition.trigger} → {transition.to}
          {transition.guards.length > 0 ? (
            <Tag tone="outline">{transition.guards.length} guard(s)</Tag>
          ) : null}
        </Button>
      ))}
    </>
  )
}

/** What has to hold, in the administrator's own five operators. */
function describe(transition: ProcessTransitionView): string {
  if (transition.guards.length === 0) {
    return `Moves to ${transition.to} with nothing to satisfy.`
  }

  return (
    `Moves to ${transition.to} when ` +
    transition.guards
      .map((guard) => `${guard.field} ${guard.operator} ${guard.value}`)
      .join(' and ') +
    '.'
  )
}

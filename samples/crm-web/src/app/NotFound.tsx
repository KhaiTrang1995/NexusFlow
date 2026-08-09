import { Link, useRouterState } from '@tanstack/react-router'
import { EmptyState } from '@/design/primitives/States'

/**
 * An address this build has no screen for.
 *
 * IT USED TO BE THE WORDS "Not Found", AND THEY WERE THE ROUTER'S. With no `notFoundComponent`
 * configured, TanStack renders `<p>Not Found</p>` — which reads, inside a CRM, as the record not
 * being there. Nothing had been asked of the server at all: no route matched a string in the
 * address bar. Those are different facts and only one of them is about the tenant's data, so this
 * says which one happened.
 *
 * NO `role="alert"`. A refusal comes from the server and carries its words; this is the client
 * saying it has no such screen, which is not an error the server has any part in.
 */
export function NotFound() {
  const pathname = useRouterState({ select: (state) => state.location.pathname })

  return (
    <div style={{ padding: '48px 24px' }}>
      <EmptyState
        title="This build has no screen at that address"
        detail={`Nothing was asked of the server: no route matches ${pathname}. A record that is missing says so on the screen that holds it.`}
        action={<Link to="/">Go to the console</Link>}
      />
    </div>
  )
}

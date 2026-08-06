import { Link, Outlet, useNavigate, useRouterState } from '@tanstack/react-router'
import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { Button, ButtonGroup } from '@/design/primitives/Button'
import { cx } from '@/lib/cx'
import { PERSONAS, useSession } from '@/session/SessionProvider'
import type { Persona } from '@/session/SessionProvider'
import { APPS, OBJECTS, SETUP_APP, appForPath } from './navigation'
import styles from './AppShell.module.css'

/**
 * The chrome every screen sits inside: brand and search, the app rail, the tab strip.
 *
 * IT RENDERS ONCE AND NEVER AGAIN. Everything below it is an `<Outlet />`, so moving between
 * screens does not remount the shell — which is what keeps the rail from flashing and the scroll
 * position of a list from being lost on the way to a record and back.
 */
export function AppShell() {
  const navigate = useNavigate()
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const currentApp = appForPath(pathname)

  // ⌘K anywhere. The prototype draws the hint; a hint for a shortcut that does nothing is worse
  // than no hint, so it is wired.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        void navigate({ to: '/search' })
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [navigate])

  return (
    <div className={styles.shell}>
      <TopBar currentApp={currentApp} />
      <div className={styles.middle}>
        <AppRail currentApp={currentApp} />
        <div className={styles.main}>
          <TabStrip />
          <main className={styles.content}>
            <Outlet />
          </main>
        </div>
      </div>
    </div>
  )
}

function TopBar({ currentApp }: { currentApp: string }) {
  const session = useSession()
  const navigate = useNavigate()
  const app = [...APPS, SETUP_APP].find((candidate) => candidate.id === currentApp)

  return (
    <header className={styles.topbar}>
      <div className={styles.brand}>
        <div className={styles.mark} aria-hidden="true">
          GoK
        </div>
        <div className={styles.brandRule} />
        <div className={styles.appLabel}>{app?.label ?? 'Sales Cloud'}</div>
      </div>

      <button type="button" className={styles.search} onClick={() => void navigate({ to: '/search' })}>
        <span className={styles.searchGlyph} aria-hidden="true">
          ⌕
        </span>
        <span>Search accounts, contacts, opportunities…</span>
        <span className={styles.kbd} aria-hidden="true">
          ⌘K
        </span>
      </button>

      <ButtonGroup label="Signed-in role" className={styles.personas}>
        {PERSONAS.map((persona) => (
          <Button
            key={persona.id}
            aria-pressed={session.persona === persona.id}
            onClick={() => switchPersona(persona.id)}
          >
            {persona.label}
          </Button>
        ))}
      </ButtonGroup>

      <div className={styles.topbarEnd}>
        <span aria-hidden="true">◔</span>
        <span aria-hidden="true">⌂</span>
        <span aria-hidden="true">?</span>
        <div className={styles.avatar} title={`${session.displayName} · signed in as ${session.persona}`}>
          {session.initials}
        </div>
      </div>
    </header>
  )

  /**
   * Switching persona also lands where that persona starts, as the prototype does — a director
   * dropped on a seller's console has to navigate before they see anything of theirs.
   */
  function switchPersona(persona: Persona) {
    session.switchTo(persona)

    const home =
      persona === 'admin'
        ? '/setup'
        : persona === 'director'
          ? '/exec'
          : persona === 'manager'
            ? '/plan/portfolio'
            : '/'

    void navigate({ to: home })
  }
}

function AppRail({ currentApp }: { currentApp: string }) {
  const session = useSession()

  return (
    <nav className={styles.rail} aria-label="Applications">
      {APPS.map((app) => (
        <RailLink key={app.id} to={app.to} label={app.label} current={currentApp === app.id}>
          <RailIcon path={app.icon} />
          <span className={styles.railLabel}>{app.short}</span>
        </RailLink>
      ))}

      <div className={styles.railFoot}>
        <div className={styles.railRule} />
        <RailLink
          to={SETUP_APP.to}
          label="Setup and configuration"
          current={currentApp === 'setup'}
        >
          <RailIcon path={SETUP_APP.icon} />
          <span className={styles.railLabel}>Setup</span>
        </RailLink>
        <div className={styles.railButton} style={{ height: 52, cursor: 'default' }}>
          <span className={styles.railAvatar}>{session.initials}</span>
          <span className={styles.railLabel} style={{ color: 'var(--color-neutral-600)' }}>
            {session.persona === 'rep'
              ? 'Rep'
              : session.persona === 'manager'
                ? 'Mgr'
                : session.persona === 'director'
                  ? 'Dir'
                  : 'Admin'}
          </span>
        </div>
      </div>
    </nav>
  )
}

function RailLink({
  to,
  label,
  current,
  children,
}: {
  to: string
  label: string
  current: boolean
  children: ReactNode
}) {
  return (
    <Link
      to={to}
      title={label}
      aria-label={label}
      aria-current={current ? 'page' : undefined}
      className={styles.railButton}
    >
      {children}
    </Link>
  )
}

function RailIcon({ path }: { path: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      width="20"
      height="20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      style={{ display: 'block' }}
    >
      <path d={path} />
    </svg>
  )
}

function TabStrip() {
  const pathname = useRouterState({ select: (state) => state.location.pathname })

  return (
    <nav className={styles.tabs} aria-label="Record types">
      <Link to="/" className={styles.tab} aria-current={pathname === '/' ? 'page' : undefined}>
        Home
      </Link>
      {OBJECTS.map((object) => (
        <Link
          key={object.key}
          to="/records/$object"
          params={{ object: object.key }}
          className={styles.tab}
          aria-current={pathname.startsWith(`/records/${object.key}`) ? 'page' : undefined}
        >
          {object.plural}
        </Link>
      ))}
      <Link
        to="/analytics/reports"
        className={cx(styles.tab)}
        aria-current={pathname.startsWith('/analytics') ? 'page' : undefined}
      >
        Reports
      </Link>
      <Link
        to="/setup"
        className={styles.tab}
        aria-current={pathname.startsWith('/setup') ? 'page' : undefined}
      >
        Setup
      </Link>

      <div className={styles.org}>
        <span className={styles.orgName}>org: gridline-prod</span>
        <span className={styles.orgBadge}>sandbox</span>
      </div>
    </nav>
  )
}

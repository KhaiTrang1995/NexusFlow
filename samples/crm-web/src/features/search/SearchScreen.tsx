import { useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  EmptyState,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  Tag,
  TextField,
} from '@/design/primitives'
import { useSearch } from '@/api/queries/hooks'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { useSession } from '@/session/SessionProvider'
import { readRecents } from '@/lib/recents'
import styles from './search.module.css'

/**
 * Search everything.
 *
 * A HIT IS AN IDENTITY, NOT A ROW. The backend answers one statement over five tables and returns
 * a kind and an id — deliberately, because a search that returned whole rows would have to decide
 * which fields to redact for whom, on every table, in the search path. Fetching the record is the
 * next click, through the surface that already knows the read policy.
 *
 * THE SERVER SEARCHES; THE CLIENT DOES NOT. Filtering the fixtures alongside would produce two
 * answers to one question and no way to tell which was right.
 */
export function SearchScreen() {
  const navigate = useNavigate()
  const [phrase, setPhrase] = useState('')
  const [kind, setKind] = useState<string | null>(null)

  const { tenantId } = useSession()
  const results = useSearch(phrase)

  // Read once per mount rather than watched: the list only changes on another screen, and a
  // storage listener here would be a subscription for a panel nobody is looking at.
  const recent = useMemo(() => readRecents(tenantId), [tenantId])

  const kinds = useMemo(
    () => [...new Set((results.data?.hits ?? []).map((hit) => hit.kind))],
    [results.data],
  )

  const hits = (results.data?.hits ?? []).filter((hit) => kind === null || hit.kind === kind)

  return (
    <Page>
      <PageHeader eyebrow="Search" title="Find anything" />

      <div className={styles.searchBox}>
        <TextField
          label="What are you looking for"
          autoFocus
          placeholder="An account, a contact, a deal, a quote…"
          value={phrase}
          onChange={(event) => setPhrase(event.target.value)}
        />
      </div>

      {kinds.length > 0 ? (
        <div className={styles.kindRow}>
          <Button size="sm" pill aria-pressed={kind === null} onClick={() => setKind(null)}>
            Everything
          </Button>
          {kinds.map((option) => (
            <Button
              key={option}
              size="sm"
              pill
              aria-pressed={kind === option}
              onClick={() => setKind(option)}
            >
              {option}
            </Button>
          ))}
        </div>
      ) : null}

      <div style={{ marginTop: 14 }}>
        {phrase.trim().length < 2 ? (
          <Panel padding="flush">
            <PanelHeader title="Recent" note="what you opened, in this browser" />
            {recent.length === 0 ? (
              <EmptyState
                title="Nothing opened yet"
                detail="Records you open appear here. This list is this browser's, not the server's — nothing in this application records that somebody looked at a row."
              />
            ) : (
              <div>
                {recent.map((entry) => (
                  <button
                    key={`${entry.objectKey}/${entry.id}`}
                    type="button"
                    className={styles.hit}
                    onClick={() =>
                      void navigate({
                        to: '/records/$object/$id',
                        params: { object: entry.objectKey, id: entry.id },
                      })
                    }
                  >
                    <div>
                      <div className={styles.hitTitle}>{entry.title}</div>
                      <div className={styles.sub}>{entry.subtitle}</div>
                    </div>
                    <Tag tone="outline" className={styles.kindTag}>
                      {OBJECT_MODELS[entry.objectKey]?.label}
                    </Tag>
                  </button>
                ))}
              </div>
            )}
          </Panel>
        ) : (
          <AsyncBoundary query={results} skeletonRows={5}>
            {(data) => (
              <Panel padding="flush">
                <PanelHeader
                  title="Results"
                  note={`${hits.length} of ${data.hits.length}${kind ? ` · ${kind}` : ''}`}
                />
                {/*
                  A KIND CHIP HIDING EVERY HIT IS NOT THE SERVER FINDING NOTHING. Both said
                  "Nothing matches “northwind”" — one truthfully, and one over twelve results the
                  reader had filtered out one click earlier and could no longer see the count of.
                  The chip is the reader's own doing, so the sentence names it and offers it back.
                */}
                {hits.length === 0 && kind !== null && data.hits.length > 0 ? (
                  <EmptyState
                    title={`No ${kind.toLowerCase()} matches “${phrase}”`}
                    detail={`The server found ${data.hits.length} of other kinds. Everything shows them.`}
                    action={
                      <Button tone="primary" onClick={() => setKind(null)}>
                        Show everything
                      </Button>
                    }
                  />
                ) : hits.length === 0 ? (
                  <EmptyState
                    title={`Nothing matches “${phrase}”`}
                    detail="Search runs over accounts, contacts, leads, opportunities and quotes. A hit is an identity — opening it fetches the record through the surface that knows the read policy."
                  />
                ) : (
                  <div>
                    {hits.map((hit) => (
                      <button
                        key={`${hit.kind}-${hit.id}`}
                        type="button"
                        className={styles.hit}
                        onClick={() =>
                          void navigate({
                            to: '/records/$object/$id',
                            params: { object: hit.kind.toLowerCase(), id: hit.id },
                          })
                        }
                      >
                        <div>
                          <div className={styles.hitTitle}>{hit.title}</div>
                          <div className={styles.sub}>{hit.id}</div>
                        </div>
                        <Tag tone="outline" className={styles.kindTag}>
                          {hit.kind}
                        </Tag>
                      </button>
                    ))}
                  </div>
                )}
              </Panel>
            )}
          </AsyncBoundary>
        )}
      </div>
    </Page>
  )
}


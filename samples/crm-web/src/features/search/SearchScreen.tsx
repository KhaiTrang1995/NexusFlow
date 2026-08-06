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

  const results = useSearch(phrase)

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
            <PanelHeader title="Recent" note="what you looked at last" />
            <div>
              {RECENT.map((entry) => (
                <button
                  key={entry.id}
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
          </Panel>
        ) : (
          <AsyncBoundary query={results} skeletonRows={5}>
            {(data) => (
              <Panel padding="flush">
                <PanelHeader
                  title="Results"
                  note={`${hits.length} of ${data.hits.length}${kind ? ` · ${kind}` : ''}`}
                />
                {hits.length === 0 ? (
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

const RECENT = [
  { objectKey: 'opportunity', id: 'O-1041', title: 'Northwind — Platform Expansion', subtitle: 'Negotiation · closes 28 Aug' },
  { objectKey: 'account', id: 'A-101', title: 'Northwind Systems', subtitle: 'Strategic · SaaS · NA' },
  { objectKey: 'contact', id: 'C-502', title: 'Ron Petrov', subtitle: 'CFO · Northwind Systems' },
  { objectKey: 'quote', id: 'Q-9002', title: 'Q-9002', subtitle: 'In Review · Cardinal — Enterprise Pilot' },
]

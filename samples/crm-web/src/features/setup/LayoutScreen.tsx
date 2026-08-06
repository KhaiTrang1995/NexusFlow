import { useState } from 'react'
import { Columns, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { modelFor } from '@/fixtures/objects'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

/**
 * What a record page shows.
 *
 * THE PALETTE IS WHAT IS NOT PLACED. A layout editor that listed every field twice — once in the
 * sections and once in a palette — makes it impossible to see at a glance which fields a record
 * page never shows, which is the one question this screen exists to answer.
 */
export function LayoutScreen() {
  const [objectKey, setObjectKey] = useState('opportunity')
  const model = modelFor(objectKey)

  const placed = new Set(model.layout.flatMap((section) => section.fields))
  const unplaced = model.fields.filter((field) => !placed.has(field.name))

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Page layout" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      <Columns layout="split">
        <Panel>
          <PanelHeader title={`${model.label} layout`} note={`${model.layout.length} sections`} />
          <div style={{ paddingTop: 14 }}>
            {model.layout.map((section) => (
              <div key={section.title} className={styles.layoutSection}>
                <div className={styles.layoutTitle}>
                  {section.title}
                  <span className={styles.sub}> · {section.columns} column{section.columns === 1 ? '' : 's'}</span>
                </div>
                <div
                  className={styles.layoutFields}
                  style={{ gridTemplateColumns: `repeat(${section.columns}, minmax(0, 1fr))` }}
                >
                  {section.fields.map((name) => {
                    const field = model.fields.find((candidate) => candidate.name === name)
                    return (
                      <div key={name} className={styles.layoutField} draggable>
                        <span aria-hidden="true" className={styles.stageHandle}>
                          ⠿
                        </span>
                        {field?.label ?? name}
                        {field?.required ? <Tag tone="accent">required</Tag> : null}
                      </div>
                    )
                  })}
                </div>
              </div>
            ))}
          </div>
        </Panel>

        <Panel padding="flush">
          <PanelHeader
            title="Not on the page"
            note={unplaced.length === 0 ? 'every field is placed' : `${unplaced.length} fields`}
          />
          <div className={styles.palette}>
            {unplaced.map((field) => (
              <span key={field.name} className={styles.paletteChip} draggable>
                {field.label}
              </span>
            ))}
            {unplaced.length === 0 ? (
              <p className={styles.sub}>
                Every field on this object appears somewhere on the record page.
              </p>
            ) : null}
          </div>
        </Panel>
      </Columns>
    </Page>
  )
}

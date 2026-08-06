import { useState } from 'react'
import { Button, Meter, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useToast } from '@/app/ToastProvider'
import { modelFor } from '@/fixtures/objects'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

/**
 * The path a record walks.
 *
 * THE PROBABILITY IS PART OF THE STAGE, NOT A SEPARATE FIELD. A stage that implies 60% and a deal
 * in it that says 45% is a weighted forecast nobody can reconcile — so the number lives with the
 * stage, and moving a deal moves both.
 */
export function StagesScreen() {
  const toast = useToast()
  const [objectKey, setObjectKey] = useState('opportunity')
  const model = modelFor(objectKey)
  const [order, setOrder] = useState<readonly string[] | null>(null)

  const stages = model.stages ?? []
  const names = order ?? stages.map((stage) => stage.name)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Stages" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher
          value={objectKey}
          onChange={(key) => {
            setObjectKey(key)
            setOrder(null)
          }}
        />
      </Panel>

      <Panel padding="flush">
        <PanelHeader
          title={`${model.label} path`}
          note={stages.length === 0 ? 'this object has no path' : `${stages.length} stages`}
          actions={
            order ? (
              <Button
                size="sm"
                tone="primary"
                onClick={() => {
                  toast.saved(`${model.label} path reordered.`)
                  setOrder(null)
                }}
              >
                Save the order
              </Button>
            ) : undefined
          }
        />
        {stages.length === 0 ? (
          <p className={styles.sub} style={{ padding: 17 }}>
            {model.plural} do not walk a path. Add a stage field to give them one.
          </p>
        ) : (
          <div>
            {names.map((name, index) => {
              const stage = stages.find((candidate) => candidate.name === name)
              if (!stage) return null

              return (
                <div key={name} className={styles.stageRow}>
                  <span className={styles.stageHandle} aria-hidden="true">
                    ⠿
                  </span>
                  <span className={styles.numeric} style={{ width: 24 }}>
                    {index + 1}
                  </span>
                  <span className={styles.stageName}>{stage.name}</span>
                  {stage.won ? <Tag tone="positive">won</Tag> : null}
                  {stage.lost ? <Tag tone="critical">lost</Tag> : null}
                  <div style={{ width: 160 }}>
                    <Meter
                      label={`${stage.name} implied probability`}
                      value={stage.pct}
                      target={100}
                      tone={stage.won ? 'positive' : 'accent'}
                    />
                  </div>
                  <span className={styles.numeric} style={{ width: 48, textAlign: 'right' }}>
                    {stage.pct}%
                  </span>
                  <Button
                    size="sm"
                    iconOnly
                    aria-label={`Move ${stage.name} earlier`}
                    disabled={index === 0}
                    onClick={() => move(index, -1)}
                  >
                    ↑
                  </Button>
                  <Button
                    size="sm"
                    iconOnly
                    aria-label={`Move ${stage.name} later`}
                    disabled={index === names.length - 1}
                    onClick={() => move(index, 1)}
                  >
                    ↓
                  </Button>
                </div>
              )
            })}
          </div>
        )}
      </Panel>
    </Page>
  )

  function move(index: number, direction: -1 | 1) {
    setOrder((current) => {
      const list = [...(current ?? names)]
      const target = index + direction
      const a = list[index]
      const b = list[target]
      if (a === undefined || b === undefined) return list
      list[index] = b
      list[target] = a
      return list
    })
  }
}

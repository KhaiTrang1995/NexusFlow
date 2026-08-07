import { Columns, DataTable, Page, PageHeader, Panel, PanelHeader, StatGrid, StatTile, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { fullMoney, money, percent } from '@/lib/format'
import { QUALIFICATION, STEPS } from './planFixtures'
import type { PlanStep } from './planFixtures'
import { PERIODS } from '@/features/exec/period'
import { PlanDetailPanels } from './PlanDetailPanels'
import styles from './planning.module.css'

/**
 * A deal plan.
 *
 * ANSWERED OR NOT, NEVER A SCORE. Eight elements, each either written down or not. A seller who
 * rates their own deal seven out of ten on "compelling event" has told you nothing; a seller who
 * cannot say what the compelling event is has told you everything — and that is a state a screen
 * can show without asking anybody to be honest about their own deal.
 */
export function OpportunityPlanScreen() {
  const deal = OBJECT_MODELS['opportunity']?.records[0]
  const answered = QUALIFICATION.filter((element) => element.answer !== null)
  const overdue = STEPS.filter((step) => step.overdue && !step.done)

  return (
    <Page>
      <PageHeader
        eyebrow="Planning · deal plan"
        title={String(deal?.['name'] ?? 'Opportunity plan')}
      />

      {/* The tenant's own plans, read whole. Everything below this is the prototype's. */}
      <PlanDetailPanels kind="Opportunity" period={PERIODS[0]} />

      <div className={styles.sampleNote}>Everything below is sample data.</div>


      <StatGrid columns={4}>
        <StatTile label="Amount" value={money(Number(deal?.['amount'] ?? 0))} note={String(deal?.['stage'])} />
        <StatTile
          label="Qualified"
          value={`${answered.length}/${QUALIFICATION.length}`}
          delta={percent(answered.length / QUALIFICATION.length)}
          direction={answered.length >= 6 ? 'up' : 'down'}
          note="elements answered"
        />
        <StatTile
          label="Overdue steps"
          value={overdue.length}
          direction={overdue.length > 0 ? 'down' : 'up'}
          note="the earliest signal a deal is slipping"
        />
        <StatTile label="Closes" value={String(deal?.['closeDate'])} note={String(deal?.['forecast'])} />
      </StatGrid>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Qualification"
            note={`${answered.length} of ${QUALIFICATION.length} answered`}
          />
          <div className={styles.qualification}>
            {QUALIFICATION.map((element) => (
              <div key={element.name} className={styles.qualRow}>
                <span
                  className={`${styles.qualMark} ${element.answer ? styles.qualAnswered : ''}`}
                  aria-hidden="true"
                >
                  {element.answer ? '✓' : '?'}
                </span>
                <div style={{ minWidth: 0 }}>
                  <div className={styles.qualName}>{element.name}</div>
                  <div className={styles.sub}>{element.question}</div>
                  {element.answer ? (
                    <p style={{ fontSize: 14, marginTop: 4 }}>{element.answer}</p>
                  ) : (
                    <p className={styles.sub} style={{ marginTop: 4, color: 'var(--color-critical)' }}>
                      Nobody has written this down.
                    </p>
                  )}
                </div>
              </div>
            ))}
          </div>
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Mutual action plan" note="what both sides agreed to do" />
          <DataTable
            caption="Plan steps"
            rows={STEPS}
            rowKey={(row) => row.what}
            columns={[
              {
                id: 'what',
                header: 'Step',
                cell: (row: PlanStep) => (
                  <>
                    <span style={{ textDecoration: row.done ? 'line-through' : undefined }}>
                      {row.what}
                    </span>
                    <div className={styles.sub}>{row.who}</div>
                  </>
                ),
              },
              {
                id: 'due',
                header: 'Due',
                cell: (row: PlanStep) =>
                  row.done ? (
                    <Tag tone="positive">Done</Tag>
                  ) : row.overdue ? (
                    <Tag tone="critical" dot>
                      {row.due} · overdue
                    </Tag>
                  ) : (
                    <span className={styles.numeric}>{row.due}</span>
                  ),
              },
            ]}
          />
        </Panel>
      </Columns>

      <Panel padding="flush">
        <PanelHeader title="What is on the deal" note="from the record" />
        <DataTable
          caption="Deal fields"
          rows={(OBJECT_MODELS['opportunity']?.fields ?? []).filter((field) => deal?.[field.name] !== undefined)}
          rowKey={(row) => row.name}
          columns={[
            { id: 'label', header: 'Field', cell: (row) => row.label },
            {
              id: 'value',
              header: 'Value',
              cell: (row) =>
                row.type === 'currency'
                  ? fullMoney(Number(deal?.[row.name]))
                  : String(deal?.[row.name] ?? '—'),
            },
          ]}
        />
      </Panel>
    </Page>
  )
}

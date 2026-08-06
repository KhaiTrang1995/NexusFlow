import { Columns, DataTable, Page, PageHeader, Panel, PanelHeader, StatGrid, StatTile, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { fullMoney, money } from '@/lib/format'
import { OBJECTIVES, RISKS, STAKEHOLDERS } from './planFixtures'
import type { Objective, Risk, Stakeholder } from './planFixtures'
import styles from './planning.module.css'

/**
 * An account plan.
 *
 * WHAT IT IS FOR, BEYOND A NUMBER. Objectives, the people who have to agree, and the risks that
 * would stop it. The stakeholder map is the part that loses B2B deals: an account where nobody
 * has met the economic buyer is not a covered account, whatever the pipeline says.
 */
export function AccountPlanScreen() {
  const account = OBJECT_MODELS['account']?.records[0]
  const opportunities = (OBJECT_MODELS['opportunity']?.records ?? []).filter(
    (row) => row['account'] === account?.['name'],
  )

  const pipeline = opportunities
    .filter((row) => !String(row['stage']).startsWith('Closed'))
    .reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0)

  const openRisks = RISKS.filter((risk) => !risk.closed)
  const unknown = STAKEHOLDERS.filter((person) => person.stance === 'Unknown' || person.stance === 'Against')

  return (
    <Page>
      <PageHeader
        eyebrow="Planning · account plan"
        title={String(account?.['name'] ?? 'Account plan')}
      />

      <StatGrid columns={4}>
        <StatTile label="ARR today" value={money(Number(account?.['arr'] ?? 0))} note={String(account?.['tier'])} />
        <StatTile label="Plan target" value={money(1_200_000)} note="the commitment for this period" />
        <StatTile
          label="Open pipeline"
          value={money(pipeline)}
          direction={pipeline >= 1_200_000 ? 'up' : 'down'}
          delta={pipeline >= 1_200_000 ? 'covered' : 'short'}
          note="against the target"
        />
        <StatTile
          label="People not with us"
          value={unknown.length}
          direction={unknown.length > 0 ? 'down' : 'up'}
          note="unknown or against"
        />
      </StatGrid>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="Objectives" note="what this plan is for, beyond a number" />
          <DataTable
            caption="Account objectives"
            rows={OBJECTIVES}
            rowKey={(row) => row.name}
            columns={[
              {
                id: 'name',
                header: 'Objective',
                cell: (row: Objective) => (
                  <>
                    <span className={styles.link}>{row.target}</span>
                    <div className={styles.sub}>
                      {row.measure} · {row.owner}
                    </div>
                  </>
                ),
              },
              {
                id: 'status',
                header: 'Status',
                cell: (row: Objective) => (
                  <Tag
                    tone={
                      row.status === 'On track'
                        ? 'positive'
                        : row.status === 'At risk'
                          ? 'warning'
                          : 'critical'
                    }
                    dot
                  >
                    {row.status}
                  </Tag>
                ),
              },
            ]}
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Stakeholders" note="who has not agreed yet" />
          <div className={styles.stakeholder}>
            {STAKEHOLDERS.map((person) => (
              <StakeholderCard key={person.name} person={person} />
            ))}
          </div>
        </Panel>
      </Columns>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Risks"
            note={`${openRisks.length} open · closed ones stay on the register`}
          />
          <DataTable
            caption="Account risks"
            rows={RISKS}
            rowKey={(row) => row.what}
            columns={[
              {
                id: 'what',
                header: 'Risk',
                cell: (row: Risk) => (
                  <>
                    <span style={{ textDecoration: row.closed ? 'line-through' : undefined }}>
                      {row.what}
                    </span>
                    <div className={styles.sub}>{row.mitigation}</div>
                  </>
                ),
              },
              {
                id: 'impact',
                header: 'Impact',
                cell: (row: Risk) => (
                  <Tag tone={row.closed ? 'neutral' : row.impact === 'High' ? 'critical' : 'warning'}>
                    {row.closed ? 'Closed' : row.impact}
                  </Tag>
                ),
              },
              { id: 'owner', header: 'Owner', cell: (row: Risk) => row.owner },
            ]}
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Open deals on this account" note={`${opportunities.length}`} />
          <DataTable
            caption="Opportunities on this account"
            rows={opportunities}
            rowKey={(row) => row.id}
            columns={[
              { id: 'name', header: 'Opportunity', cell: (row) => row['name'] },
              { id: 'stage', header: 'Stage', cell: (row) => <Tag tone="outline">{row['stage']}</Tag> },
              {
                id: 'amount',
                header: 'Amount',
                numeric: true,
                cell: (row) => fullMoney(Number(row['amount'])),
                sortValue: (row) => Number(row['amount'] ?? 0),
              },
            ]}
            empty="Nothing open on this account."
          />
        </Panel>
      </Columns>
    </Page>
  )
}

function StakeholderCard({ person }: { person: Stakeholder }) {
  const tone =
    person.stance === 'Advocate'
      ? 'positive'
      : person.stance === 'Against'
        ? 'critical'
        : person.stance === 'Unknown'
          ? 'warning'
          : 'neutral'

  return (
    <div className={styles.stakeholderCard}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
        <strong style={{ fontWeight: 500 }}>{person.name}</strong>
        <Tag tone={tone} dot>
          {person.stance}
        </Tag>
      </div>
      <div className={styles.sub}>{person.title}</div>
      <div className={styles.sub}>
        {person.role} · last seen {person.lastSeen}
      </div>
    </div>
  )
}

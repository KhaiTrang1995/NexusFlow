import { useNavigate } from '@tanstack/react-router'
import { DataTable, EmptyState, Panel, PanelHeader, Skeleton } from '@/design/primitives'
import { useRelatedRecords } from '@/api/queries/hooks'
import { modelFor } from '@/fixtures/objects'
import type { RecordRow } from '@/fixtures/objects'
import { renderCell } from './RecordCell'
import { toRows } from './liveRecords'
import type { RelatedLink } from './related'

/**
 * One related list, read from the server by the foreign key that makes it related.
 *
 * ONE QUERY PER LIST, NOT ONE PER ROW. Each panel asks for the children of this record and no
 * others; the alternative — pulling every quote in the tenant and filtering here — is a related
 * list that gets slower as somebody else's pipeline grows.
 *
 * AN EMPTY LIST SAYS SO, AND A FAILED ONE DOES NOT PRETEND TO BE EMPTY. Those are different
 * facts about the same panel, and a build that drew both as "nothing here" is how a reader
 * concludes a deal has no quotes when the read was refused.
 */
export function RelatedList({ link, parentId }: { link: RelatedLink; parentId: string }) {
  const navigate = useNavigate()
  const page = useRelatedRecords(link.entity, link.field, parentId)
  const model = modelFor(link.objectKey)

  const rows = toRows(link.objectKey, model, page.data?.records ?? [])

  if (page.isPending) {
    return (
      <Panel>
        <Skeleton rows={2} />
      </Panel>
    )
  }

  if (page.isError) {
    return (
      <Panel>
        <EmptyState
          title={`${link.title} could not be read`}
          detail={page.error.message}
        />
      </Panel>
    )
  }

  if (rows.length === 0) {
    return null
  }

  return (
    <Panel padding="flush">
      <PanelHeader title={link.title} note={`${rows.length}`} />
      <DataTable
        caption={link.title}
        columns={link.columns.map((name) => ({
          id: name,
          header: model.fields.find((field) => field.name === name)?.label ?? name,
          cell: (row: RecordRow) => renderCell(model, row, name),
        }))}
        rows={rows}
        rowKey={(row) => row.id}
        onRowClick={(row) =>
          void navigate({
            to: '/records/$object/$id',
            params: { object: link.objectKey, id: row.id },
          })
        }
      />
    </Panel>
  )
}

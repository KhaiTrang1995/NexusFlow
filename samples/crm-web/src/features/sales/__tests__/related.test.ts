import { describe, expect, it } from 'vitest'
import { relatedLinksOf } from '../related'
import { entityOf } from '../liveRecords'
import { modelFor } from '@/fixtures/objects'

/**
 * The related lists, checked against the two things they can silently disagree with.
 *
 * A wrong `field` is a filter the server refuses, which the panel shows. A wrong `columns` entry
 * is a header the model has no label for and a cell that renders as an em dash — which looks like
 * a child with no data rather than a link pointing at a column that does not exist.
 */
describe('relatedLinksOf', () => {
  it('names what points at each kind, and nothing at a lead', () => {
    expect(relatedLinksOf('account').map((link) => link.objectKey)).toEqual([
      'contact',
      'opportunity',
    ])
    expect(relatedLinksOf('opportunity').map((link) => link.objectKey)).toEqual(['quote'])
    expect(relatedLinksOf('quote').map((link) => link.objectKey)).toEqual(['workorder'])

    // A lead has no children until it is converted, and then it becomes three records that point
    // at each other. An empty entry would suggest the list exists.
    expect(relatedLinksOf('lead')).toEqual([])
  })

  it('links only to objects the server can page', () => {
    for (const parent of ['account', 'opportunity', 'quote']) {
      for (const link of relatedLinksOf(parent)) {
        expect(entityOf(link.objectKey), `${parent} → ${link.objectKey}`).toBe(link.entity)
      }
    }
  })

  it('names only columns the child object actually has', () => {
    for (const parent of ['account', 'opportunity', 'quote']) {
      for (const link of relatedLinksOf(parent)) {
        const model = modelFor(link.objectKey)

        for (const column of link.columns) {
          expect(
            model.fields.some((field) => field.name === column),
            `${link.objectKey} has no field '${column}'`,
          ).toBe(true)
        }
      }
    }
  })
})

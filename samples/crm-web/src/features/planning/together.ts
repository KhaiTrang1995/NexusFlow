/**
 * Two reads, guarded as one.
 *
 * <strong>A panel drawn from two requests has to be guarded by both, and the capacity screen was
 * guarded by one.</strong> Its tiles and its team table are computed from the quota rows and the
 * reporting line; only the quotas sat inside an {@link AsyncBoundary}. A refused `/org/chart` left
 * the org chart empty, which is not an error to `capacityOf` — every seller's manager is then
 * unknown, so all of them collapse into a single "Top of the line" row and the screen draws one
 * team that does not exist, with the right money in it.
 *
 * <strong>The error reported is the first one, not a summary.</strong> Two failures produce one
 * problem document on the screen and it is a real one, with the server's own code and sentence.
 * "Two requests failed" is a sentence no reader can act on.
 */
export interface Readable<T> {
  data: T | undefined
  isPending: boolean
  isError: boolean
  error: unknown
  refetch: () => void
}

export function together<A, B>(left: Readable<A>, right: Readable<B>): Readable<[A, B]> {
  const failed = left.isError ? left : right.isError ? right : null

  return {
    data:
      left.data === undefined || right.data === undefined
        ? undefined
        : [left.data, right.data],

    // Not pending once either has failed. One refusal and one request still in flight is a pair
    // that is already decided, and reporting it as pending hides the refusal behind a skeleton
    // until the other request happens to come back.
    isPending: failed === null && (left.isPending || right.isPending),
    isError: failed !== null,
    error: failed?.error,

    // Both, because the reader pressed one button and expects the panel to come back. Retrying
    // only the failed one leaves the other on whatever it last answered.
    refetch: () => {
      left.refetch()
      right.refetch()
    },
  }
}

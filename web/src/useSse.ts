import { useEffect } from 'react'

// The 11 v1 event types (spec §10.2). EventSource cannot send an Authorization header, which is
// exactly why the API falls back to the ed_session cookie — so this needs no token plumbing.
export type DocEvent =
  | 'version.created'
  | 'version.published'
  | 'merge.completed'
  | 'diff.ready'
  | 'member.added'
  | 'push.requested'
  | 'push.reviewed'
  | 'approval.responded'
  | 'pdf.ready'
  | 'version.named'
  | 'version.reverted'

const TYPES: DocEvent[] = [
  'version.created',
  'version.published',
  'merge.completed',
  'diff.ready',
  'member.added',
  'push.requested',
  'push.reviewed',
  'approval.responded',
  'pdf.ready',
  'version.named',
  'version.reverted',
]

/**
 * One EventSource per document, subscribed to all eleven v1 event types and closed on cleanup.
 *
 * `onEvent` MUST be stable — wrap it in useCallback. It is in the effect's dependency list (honestly:
 * a stale callback would push updates into a dead render), so a fresh function identity on every
 * render would tear the stream down and reconnect on every render.
 *
 * ponytail: every event triggers the same refetch instead of patching local state from the event
 * payload, so the caller can ignore which event arrived. The console's reads are two cheap indexed
 * queries, and a burst collapses into one render pass. Upgrade path if a profile ever complains:
 * switch on the type and patch the row the payload names.
 */
export function useSse(documentId: string | undefined, onEvent: (e: DocEvent) => void) {
  useEffect(() => {
    if (!documentId) return
    const source = new EventSource(`/api/v1/documents/${documentId}/events`)
    for (const type of TYPES) source.addEventListener(type, () => onEvent(type))
    // An error leaves the stream alone: EventSource reconnects on its own, and a document the caller
    // cannot read (403/404) simply never delivers an event.
    return () => source.close()
  }, [documentId, onEvent])
}

import { useCallback, useEffect, useState } from 'react'
import { useOutletContext, useParams } from 'react-router'
import { api, problemText, type AuditRow, type Paged } from '../api'

// The audit tab (spec §9, §11): this document's slice of the append-only trail, newest first. Every member
// may read it — a trail only the owner can see does not answer "what happened to my document".
export default function Audit() {
  const { id } = useParams()
  const { tick } = useOutletContext<{ tick: number }>()
  const [rows, setRows] = useState<AuditRow[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(
    async (cursor: string | null) => {
      const params = new URLSearchParams()
      if (cursor) params.set('cursor', cursor)
      const page = await api.get<Paged<AuditRow>>(`/api/v1/documents/${id}/audit?${params}`)
      setRows((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
    },
    [id],
  )

  useEffect(() => {
    load(null).catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  return (
    <div data-testid="audit">
      <h3>Audit</h3>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      <ol className="rows">
        {rows.map((e) => (
          <li key={e.id} data-testid="audit-row" data-action={e.action}>
            <span className="version-number" data-testid="audit-action">
              {e.action}
            </span>
            {/* A share-link read has no actor at all: actorUserId and actorName are both null, so it was
                nobody, not somebody unresolvable. "(unknown)" — which the API does send when an id
                resolves to no user — would be a different claim, and a false one here. */}
            <span data-testid="audit-actor">{e.actorName ?? 'anonymous'}</span>
            <time dateTime={e.createdAt}>{new Date(e.createdAt).toLocaleString()}</time>
            {/* metadata carries the human-readable part of most rows (a name, a number, a status).
                targetId is deliberately not rendered: it is a bare id with nothing to resolve it against
                on this screen, and a column of GUIDs tells a reader nothing. */}
            {e.metadata && <code className="audit-meta">{e.metadata}</code>}
          </li>
        ))}
      </ol>

      {rows.length === 0 && !error && <p>Nothing recorded yet.</p>}

      {nextCursor && (
        <button
          type="button"
          onClick={() => {
            load(nextCursor).catch((e: unknown) => setError(problemText(e)))
          }}
        >
          Load more
        </button>
      )}
    </div>
  )
}

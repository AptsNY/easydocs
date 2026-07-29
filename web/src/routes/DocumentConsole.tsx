import { useCallback, useEffect, useState } from 'react'
import { NavLink, Outlet, useParams } from 'react-router'
import { api, problemText, type DocumentDetail } from '../api'
import MembersPanel from '../components/MembersPanel'
import { useSse } from '../useSse'

// Spec §9's document console: the document's name, the tab strip, whichever tab is open, and the
// members panel beside it.
//
// `tick` is the live-update signal, and it reaches the children through the outlet context rather than
// a store: the SSE stream is document-scoped so the EventSource belongs to the console, and React
// Router already carries data down to a nested route — nothing left for a state library to do.
export default function DocumentConsole() {
  const { id } = useParams()
  const [doc, setDoc] = useState<DocumentDetail | null>(null)
  const [error, setError] = useState('')
  const [tick, setTick] = useState(0)

  // Stable, or useSse would tear the stream down and reopen it on every render.
  const bump = useCallback(() => setTick((t) => t + 1), [])
  useSse(id, bump)

  useEffect(() => {
    if (!id) return
    setError('')
    api
      .get<DocumentDetail>(`/api/v1/documents/${id}`)
      .then(setDoc, (e: unknown) => setError(problemText(e)))
  }, [id, tick])

  return (
    <section className="console" data-testid="document-console">
      <div className="console-main">
        <h2>{doc?.name ?? 'Document'}</h2>
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}

        <nav aria-label="Document sections">
          <NavLink to={`/documents/${id}`} end>
            History
          </NavLink>
          <NavLink to={`/documents/${id}/major-versions`}>Major Versions</NavLink>
          <NavLink to={`/documents/${id}/copies`}>Copies</NavLink>
          <NavLink to={`/documents/${id}/approvals`}>Approvals</NavLink>
          <NavLink to={`/documents/${id}/audit`}>Audit</NavLink>
        </nav>

        <Outlet context={{ tick }} />
      </div>

      <MembersPanel documentId={id} tick={tick} />
    </section>
  )
}

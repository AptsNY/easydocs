import { useCallback, useEffect, useState } from 'react'
import { NavLink, Outlet, useParams } from 'react-router'
import { api, problemText, type DocRole, type DocumentDetail, type Member } from '../api'
import { useSession } from '../auth'
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
  const { me } = useSession()
  const [doc, setDoc] = useState<DocumentDetail | null>(null)
  const [myRole, setMyRole] = useState<DocRole | null>(null)
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

  // The caller's own role, resolved once for the whole console: the roster is the only place it is stated
  // (org role grants nothing on a document), and the Actions menu needs it per row.
  //
  // ponytail: this is a second GET of /members, since MembersPanel reads the same roster for its own list.
  // Two cheap indexed reads beat threading a callback up out of the panel, which Task 12 owns. Upgrade path
  // when a third consumer appears: hoist the roster into this component and pass it down as a prop.
  useEffect(() => {
    if (!id || !me) return
    api.get<Member[]>(`/api/v1/documents/${id}/members`).then(
      (ms) => setMyRole(ms.find((m) => m.userId === me.id)?.role ?? null),
      () => setMyRole(null),
    )
  }, [id, me, tick])

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

        <Outlet context={{ tick, myRole }} />
      </div>

      <MembersPanel documentId={id} tick={tick} />
    </section>
  )
}

import { useCallback, useEffect, useState } from 'react'
import { Link, NavLink, Outlet, useParams } from 'react-router'
import {
  api,
  problemText,
  type DocRole,
  type DocumentDetail,
  type Member,
  type Paged,
  type VersionRow,
} from '../api'
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
  const [head, setHead] = useState<string | null>(null)
  const [memberCount, setMemberCount] = useState<number | null>(null)
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
      (ms) => {
        setMyRole(ms.find((m) => m.userId === me.id)?.role ?? null)
        setMemberCount(ms.length)
      },
      () => {
        setMyRole(null)
        setMemberCount(null)
      },
    )
  }, [id, me, tick])

  // The header's one number: which version this document is AT. The detail projection deliberately
  // carries no counts, so the head comes from the version list — limit=1 on an indexed, already-hot
  // read.
  //
  // ponytail: no "N versions" beside it, because the paged versions endpoint answers no total and
  // counting would mean walking every page. Upgrade path: versionCount on the document detail
  // projection (the dashboard's Tile already has one), then this is a field, not a request.
  useEffect(() => {
    if (!id) return
    api
      .get<Paged<VersionRow>>(`/api/v1/documents/${id}/versions?order=desc&limit=1`)
      .then((page) => setHead(page.items[0]?.number ?? null), () => setHead(null))
  }, [id, tick])

  return (
    <section className="console" data-testid="document-console">
      <div className="console-main">
        {/* Where am I. Two levels is the whole hierarchy this product has.

            ponytail: the middle crumb is called "Folder" rather than its name — there is no
            GET /folders/{id}, and the console holds no folder tree to look one up in. Upgrade path:
            a single-folder read, or folderName on the document detail projection. */}
        <nav className="crumbs" aria-label="Breadcrumb">
          <Link to="/">Documents</Link>
          {doc?.folderId ? (
            <>
              <span aria-hidden="true">/</span>
              <Link to={`/folders/${doc.folderId}`}>Folder</Link>
            </>
          ) : null}
        </nav>

        <h2>{doc?.name ?? 'Document'}</h2>

        <p className="doc-meta">
          <span className="version-number" data-testid="doc-head-version">
            {head ?? '—'}
          </span>
          <span>{head ? 'current version' : 'no versions yet'}</span>
          {memberCount === null ? null : (
            <span data-testid="doc-member-count">
              {memberCount} {memberCount === 1 ? 'member' : 'members'}
            </span>
          )}
        </p>

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

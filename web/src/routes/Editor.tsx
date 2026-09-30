import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { api, problemText, type DocumentDetail, type Paged, type VersionDetail, type VersionRow } from '../api'
import { useSession } from '../auth'
import EditorRail from '../components/EditorRail'
import { useRead } from '../useRead'
import { useCollabora } from '../useCollabora'
import { useSse } from '../useSse'

type Session = { sessionId: string; editorUrl: string; accessToken: string; accessTokenTtlSeconds: number }

// The Collabora host page (spec §6; layout spec 2026-09-29): a slim document bar, the editor filling the
// rest of the window, and a collapsible easydocs rail.
//
// Minting happens here rather than in the Actions menu because every arrival at this URL needs a session:
// a bookmark and a reload as much as a menu click.
//
// The page never closes the session. Closing revokes the WOPI token at once, and Collabora uploads its
// last save AFTER telling the page it saved, so any close from here can race that upload and lose the
// edit. An open session costs a row and a token that expires on its own (WopiAccessToken.TtlSeconds).
export default function Editor() {
  const { vid } = useParams()
  const navigate = useNavigate()
  const { me } = useSession()
  const [session, setSession] = useState<Session | null>(null)
  const [error, setError] = useState('')
  const [tick, setTick] = useState(0)
  const frame = useRef<HTMLIFrameElement>(null)

  useEffect(() => {
    if (!vid) return
    let live = true
    api.post<Session>(`/api/v1/versions/${vid}/sessions`).then(
      (s) => live && setSession(s),
      // A Viewer who follows this URL directly gets 403 "Editor role required." Say so; a blank frame
      // would look like Collabora is broken.
      (e: unknown) => live && setError(problemText(e, 'Could not open the editor.')),
    )
    return () => {
      live = false
    }
  }, [vid])

  const version = useRead<VersionDetail>(vid ? `/api/v1/versions/${vid}` : null)
  const documentId = version.data?.documentId
  const doc = useRead<DocumentDetail>(documentId ? `/api/v1/documents/${documentId}` : null)
  const bump = useCallback(() => setTick((t) => t + 1), [])
  useSse(documentId, bump)
  const page = useRead<Paged<VersionRow>>(documentId ? `/api/v1/documents/${documentId}/versions?order=desc` : null, tick)
  const versions = useMemo(() => ({ data: page.data?.items, failed: page.failed }), [page])

  // "Saved as" names only a version this page's editing produced: an editor save by me that did not
  // exist when the page first loaded the list. Not a co-editor's, not one from another tab earlier.
  const before = useRef<Set<string> | null>(null)
  if (versions.data && !before.current) before.current = new Set(versions.data.map((v) => v.id))
  const mine = versions.data?.find(
    (v) => v.source === 'EditWopi' && v.createdBy === me?.id && !before.current?.has(v.id),
  )

  const origin = useMemo(() => (session ? new URL(session.editorUrl).origin : null), [session])
  const { status, hidden, save } = useCollabora(frame, origin)

  const done = async () => {
    if ((await save()) === 'failed') return
    void navigate(documentId ? `/documents/${documentId}` : '/')
  }

  if (error)
    return (
      <section data-testid="editor" className="editor">
        <p role="alert" className="error">
          {error}
        </p>
      </section>
    )

  const label = version.data ? `${version.data.major}.${version.data.minor}.${version.data.revision}` : ''
  const statusText = {
    loading: 'Opening…',
    saved: mine ? `Saved as ${mine.number}` : 'Saved',
    unsaved: 'Unsaved changes',
    saving: 'Saving…',
    failed: "Couldn't save. Your changes are still in the editor.",
  }[status]

  return (
    <section data-testid="editor" className="editor editor-page">
      <div className="editor-bar" data-testid="editor-bar">
        {/* Same path as Done: save first, then leave. */}
        <button type="button" className="link" onClick={() => void done()}>
          ← {doc.data?.name ?? 'Document'}
        </button>
        {label && <span className="editor-bar-from">editing from {label}</span>}
        {!hidden && (
          <span role="status" data-testid="editor-status" className={`editor-status editor-status--${status}`}>
            {statusText}
          </span>
        )}
        <button type="button" onClick={() => void done()} disabled={status === 'saving'}>
          Done
        </button>
      </div>

      {session ? (
        // The src attribute is the whole contract on this side. Collabora is a separate product and may be
        // absent in a dev or CI environment, in which case this frame simply fails to load.
        <iframe
          ref={frame}
          data-testid="editor-frame"
          className="editor-frame editor-frame--fill"
          title="Document editor"
          src={session.editorUrl}
          allow="fullscreen"
        />
      ) : (
        <p className="editor-opening">Opening the editor…</p>
      )}

      {documentId && vid && <EditorRail documentId={documentId} versionId={vid} versions={versions} tick={tick} />}
    </section>
  )
}

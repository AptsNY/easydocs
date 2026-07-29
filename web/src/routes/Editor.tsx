import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { api, problemText } from '../api'

type Session = { sessionId: string; editorUrl: string; accessToken: string; accessTokenTtlSeconds: number }

// The Collabora host page (spec §6): mint an edit session for one version, hand Collabora the WOPI URL the
// API built, and close the session on the way out.
//
// Minting happens here rather than in the Actions menu because every arrival at this URL needs a session —
// a bookmark and a reload as much as a menu click.
export default function Editor() {
  const { vid } = useParams()
  const navigate = useNavigate()
  const [session, setSession] = useState<Session | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!vid) return
    let live = true
    const minting = api.post<Session>(`/api/v1/versions/${vid}/sessions`)

    minting.then(
      (s) => {
        if (live) setSession(s)
      },
      (e: unknown) => {
        // A Viewer who follows this URL directly gets 403 "Editor role required." Say so; a blank frame
        // would look like Collabora is broken.
        if (live) setError(problemText(e, 'Could not open the editor.'))
      },
    )

    // The ONE place a session is closed, so it holds whether the mint has resolved yet or not, and whether
    // this is a real unmount or StrictMode's simulated one in development (which would otherwise leak a
    // session nothing will ever render).
    return () => {
      live = false
      void minting.then(close, () => {})
    }
  }, [vid])

  return (
    <section data-testid="editor" className="editor">
      {/* History, not a location: this route does not carry the document id, so ".." would resolve to a
          non-route and land on the dashboard rather than the console the reader came from. */}
      <p>
        <button type="button" className="link" onClick={() => void navigate(-1)}>
          Back
        </button>
      </p>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {session && (
        // The src attribute is the whole contract on this side. Collabora is a separate product and may be
        // absent in a dev or CI environment, in which case this frame simply fails to load — which is not
        // this component's bug to fix, and not something a test of this component should require.
        <iframe
          data-testid="editor-frame"
          className="editor-frame"
          title="Document editor"
          src={session.editorUrl}
          allow="fullscreen"
        />
      )}

      {!session && !error && <p>Opening the editor…</p>}
    </section>
  )
}

// Best effort: DELETE is 404 for anyone but the session's owner (deliberate, so session existence does not
// leak), and a session left open costs nothing but a stale row.
const close = (s: Session) => api.del(`/api/v1/sessions/${s.sessionId}`).catch(() => {})

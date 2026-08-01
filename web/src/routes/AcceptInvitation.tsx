import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { api, problemText } from '../api'
import { useSession } from '../auth'

// Redeeming an invitation (spec §10.1 Auth, POST /invitations/{token}:accept).
//
// Both places that mint an invitation — the document Members panel and Settings → Organization — show
// the raw token exactly once and tell the inviter to send it on. Until this screen existed there was
// nowhere to send it TO: the endpoint was live but no part of the SPA ever called it, so an invited
// colleague could not join from a browser at all. That made the multi-user half of the product —
// members, approvals, concurrent branches, push-back review — reachable only via an HTTP client.
//
// The route sits inside RequireAuth on purpose. Accepting binds the invitation to the signed-in
// identity (the API 403s if the session's email is not the invited one), so there must BE a session
// first. RequireAuth carries this path through the sign-in, so a recipient who is signed out lands on
// /login and is returned here afterwards with the token intact.
export default function AcceptInvitation() {
  const { token = '' } = useParams()
  const navigate = useNavigate()
  const { refresh } = useSession()
  const [error, setError] = useState('')
  // Accepting is a mutation and StrictMode double-invokes effects in development; the second call
  // would hit the API's 409 "already accepted" and show the user a failure for a success.
  const sent = useRef(false)

  useEffect(() => {
    if (sent.current) return
    sent.current = true
    void (async () => {
      try {
        const joined = await api.post<{ orgId: string; documentId: string | null }>(
          `/api/v1/invitations/${encodeURIComponent(token)}:accept`,
        )
        // Accepting rebinds the session cookie to the inviting org, so the cached /me and /org in the
        // session context are now stale — the header would name the wrong organization.
        await refresh()
        navigate(joined.documentId ? `/documents/${joined.documentId}` : '/', { replace: true })
      } catch (e) {
        setError(problemText(e))
      }
    })()
  }, [token, navigate, refresh])

  return (
    <section className="stack" data-testid="accept-invitation">
      <h1>Invitation</h1>
      {error ? (
        <>
          <p role="alert" className="error">
            {error}
          </p>
          <p className="muted">
            An invitation can only be accepted by the person it was sent to, and each one works once.
            Ask whoever invited you to issue a new token for this account.
          </p>
          <Link to="/">Go to your documents</Link>
        </>
      ) : (
        <p role="status">Accepting your invitation…</p>
      )}
    </section>
  )
}

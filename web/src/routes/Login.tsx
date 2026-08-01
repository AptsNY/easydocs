import { useId, useState } from 'react'
import { Navigate, useLocation } from 'react-router'
import { ApiError } from '../api'
import { useSession } from '../auth'

// Where to land after signing in: the destination RequireAuth was guarding, or the dashboard. Only
// same-origin paths are honoured — `//evil.example` is a protocol-relative URL the browser would treat
// as another origin, so it is rejected rather than turned into an open redirect.
function returnTo(state: unknown): string {
  const from = (state as { from?: unknown } | null)?.from
  return typeof from === 'string' && from.startsWith('/') && !from.startsWith('//') ? from : '/'
}

// Sign-in and registration on one screen (spec §9). Registration also creates the org, so it needs the
// extra org-name field — there is no separate org-creation flow.
export default function Login() {
  const { me, signIn, register } = useSession()
  const location = useLocation()
  const [creating, setCreating] = useState(false)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [orgName, setOrgName] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const ids = useId()

  if (me) return <Navigate to={returnTo(location.state)} replace />

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError('')
    setBusy(true)
    try {
      if (creating) await register(email, displayName, password, orgName)
      else await signIn(email, password)
    } catch (err) {
      // Surface the problem+json `detail` — "Email or password is incorrect." beats a generic message.
      setError(err instanceof ApiError ? err.detail || err.title : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="auth" data-testid="login">
      <h1>easydocs</h1>
      <form onSubmit={submit} className="stack">
        <h2>{creating ? 'Create an organization' : 'Sign in'}</h2>

        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}

        <label htmlFor={`${ids}-email`}>Email</label>
        <input
          id={`${ids}-email`}
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
        />

        <label htmlFor={`${ids}-password`}>Password</label>
        <input
          id={`${ids}-password`}
          type="password"
          autoComplete={creating ? 'new-password' : 'current-password'}
          required
          // The API rejects anything shorter; failing in the browser saves a round trip.
          minLength={creating ? 12 : undefined}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />

        {creating && (
          <>
            <label htmlFor={`${ids}-name`}>Your name</label>
            <input
              id={`${ids}-name`}
              autoComplete="name"
              required
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />

            <label htmlFor={`${ids}-org`}>Organization name</label>
            <input
              id={`${ids}-org`}
              required
              value={orgName}
              onChange={(e) => setOrgName(e.target.value)}
            />
          </>
        )}

        <button type="submit" disabled={busy}>
          {creating ? 'Create account' : 'Sign in'}
        </button>

        <button
          type="button"
          className="link"
          onClick={() => {
            setCreating(!creating)
            setError('')
          }}
        >
          {creating ? 'I already have an account' : 'Create a new organization'}
        </button>
      </form>
    </main>
  )
}

import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { api, problemText } from '../api'

// The public end of an admin-issued password reset (spec 2026-09-08). Public on purpose: whoever opens
// this is locked out by definition, so it sits outside RequireAuth next to /s/{token} and renders with
// no session at all.
//
// No app chrome, for the same reason ShareLanding has none: this person cannot sign in, so a header
// full of links they cannot follow is a wall of dead ends.
//
// The token is read from the path (it is the link the admin sent) but POSTed in the BODY. That is not
// cosmetic — RateLimits.Auth partitions on request path, so a token-in-path endpoint would give every
// guess its own fresh bucket. See PasswordResetEndpoints.
export default function PasswordReset() {
  const { token } = useParams()
  const navigate = useNavigate()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const data = new FormData(e.currentTarget)
    const password = String(data.get('password') ?? '')
    const confirm = String(data.get('confirm') ?? '')

    // Checked here as well as server-side: the server cannot see the confirmation field at all, and
    // the length rule is worth saying before a round trip rather than after one.
    if (password !== confirm) return setError('The two passwords do not match.')
    if (password.length < 12) return setError('Use at least 12 characters.')

    setBusy(true)
    setError('')
    api.post<void>('/api/v1/auth/password-reset:complete', { token, password }).then(
      // Deliberately no session: the API issues no cookie, so this lands on the sign-in screen and the
      // new password gets used once immediately — which is also what makes MFA challenge, if armed.
      () => navigate('/login', { replace: true }),
      (err: unknown) => {
        setBusy(false)
        // Unknown, expired, already-used and no-longer-eligible are one 404 in the API, on purpose.
        // The message says what to do about it rather than guessing which one happened.
        setError(problemText(err, 'This reset link is no longer valid. Ask for a new one.'))
      },
    )
  }

  return (
    <main className="share" data-testid="password-reset">
      <p className="share-brand">
        <img className="brand-mark" src="/favicon.svg" alt="" />
        easydocs
      </p>

      <div className="share-card">
        <h1>Choose a new password</h1>
        <p className="muted">
          This link works once, and only for a short time after it was issued.
        </p>

        <form className="stack" onSubmit={submit}>
          <label htmlFor="new-password">New password</label>
          <input
            id="new-password"
            name="password"
            type="password"
            autoComplete="new-password"
            minLength={12}
            required
          />

          <label htmlFor="confirm-password">Confirm new password</label>
          <input
            id="confirm-password"
            name="confirm"
            type="password"
            autoComplete="new-password"
            minLength={12}
            required
          />

          <button type="submit" disabled={busy} data-testid="password-reset-submit">
            {busy ? 'Setting…' : 'Set password'}
          </button>
        </form>

        {error && (
          <p role="alert" className="error" data-testid="password-reset-error">
            {error}
          </p>
        )}

        <p className="muted">
          <Link to="/login">Back to sign in</Link>
        </p>
      </div>
    </main>
  )
}

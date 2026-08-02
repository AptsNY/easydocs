import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, problemText } from '../api'

type Status = { enabled: boolean; recoveryCodesLeft: number }
type Setup = { secret: string; otpauthUri: string }

// Two-factor authentication (issue #10). Setup mirrors the API's two steps: mint a pending secret,
// then prove the authenticator has it with one code. The recovery codes render exactly once —
// the server only ever stores their hashes.
//
// ponytail: the secret is shown as text + an otpauth: link, no QR code image. Every authenticator
// accepts manual entry; a QR needs an encoder dependency. Add one when someone actually asks.
export default function MfaSection() {
  const [status, setStatus] = useState<Status | null>(null)
  const [setup, setSetup] = useState<Setup | null>(null)
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    setStatus(await api.get<Status>('/api/v1/account/mfa'))
  }, [])
  useEffect(() => {
    load().catch((e: unknown) => setError(problemText(e)))
  }, [load])

  const act = (fn: () => Promise<void>) => {
    setError('')
    fn()
      .then(load)
      .catch((e: unknown) => setError(problemText(e)))
  }

  const begin = () =>
    act(async () => setSetup(await api.post<Setup>('/api/v1/account/mfa/setup', {})))

  const enable = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const code = new FormData(e.currentTarget).get('code') as string
    act(async () => {
      const res = await api.post<{ recoveryCodes: string[] }>('/api/v1/account/mfa/enable', { code })
      setSetup(null)
      setRecoveryCodes(res.recoveryCodes)
    })
  }

  const disable = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const code = new FormData(e.currentTarget).get('code') as string
    act(async () => {
      await api.post('/api/v1/account/mfa/disable', { code })
      setRecoveryCodes(null)
    })
  }

  return (
    <section data-testid="mfa-section">
      <h3>Two-factor authentication</h3>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {status && !status.enabled && !setup && (
        <p>
          Off.{' '}
          <button type="button" data-testid="mfa-begin" onClick={begin}>
            Set up
          </button>
        </p>
      )}

      {setup && (
        <form onSubmit={enable} className="stack" data-testid="mfa-enable">
          <p>
            Add this secret to your authenticator app (
            <a href={setup.otpauthUri}>open in an authenticator</a>):{' '}
            <code data-testid="mfa-secret">{setup.secret}</code>
          </p>
          <label>
            Code from the app
            <input name="code" autoComplete="one-time-code" inputMode="numeric" required />
          </label>
          <button type="submit">Turn on</button>
        </form>
      )}

      {recoveryCodes && (
        <div data-testid="mfa-recovery-codes">
          <p>
            <strong>Recovery codes — save them now.</strong> Each works once; this is the only time
            they are shown.
          </p>
          <ul>
            {recoveryCodes.map((c) => (
              <li key={c}>
                <code>{c}</code>
              </li>
            ))}
          </ul>
        </div>
      )}

      {status?.enabled && (
        <>
          <p>
            On — {status.recoveryCodesLeft} recovery {status.recoveryCodesLeft === 1 ? 'code' : 'codes'}{' '}
            left.
          </p>
          <form onSubmit={disable} className="stack" data-testid="mfa-disable">
            <label>
              Current code (or a recovery code) to turn off
              <input name="code" autoComplete="one-time-code" required />
            </label>
            <button type="submit">Turn off</button>
          </form>
        </>
      )}
    </section>
  )
}

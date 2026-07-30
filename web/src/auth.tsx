import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { Navigate, Outlet } from 'react-router'
import { api, ApiError, type Me, type Org } from './api'

type Session = {
  me: Me | null
  org: Org | null
  loading: boolean
  signIn: (email: string, password: string) => Promise<void>
  register: (email: string, displayName: string, password: string, orgName: string) => Promise<void>
  signOut: () => void
  refresh: () => Promise<void>
}

const SessionContext = createContext<Session | null>(null)

export function useSession(): Session {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used inside a SessionProvider')
  return ctx
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [org, setOrg] = useState<Org | null>(null)
  const [loading, setLoading] = useState(true)

  // There is no token to store: the session is an httpOnly ed_session cookie, so "am I signed in?" is
  // only answerable by asking the server. A 401 from /me is the normal signed-out state, not an error.
  const refresh = useCallback(async () => {
    try {
      const who = await api.get<Me>('/api/v1/me')
      setMe(who)
      setOrg(await api.get<Org>('/api/v1/org'))
    } catch (e) {
      if (!(e instanceof ApiError && e.status === 401)) throw e
      setMe(null)
      setOrg(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    // A probe that fails for any reason other than 401 (server down, offline) leaves `me` null, which
    // lands the visitor on /login — nothing better to do, but don't leave an unhandled rejection behind.
    refresh().catch((e: unknown) => console.error('session probe failed', e))
  }, [refresh])

  const signIn = useCallback(
    async (email: string, password: string) => {
      await api.post<Me>('/api/v1/auth/login', { email, password })
      await refresh()
    },
    [refresh],
  )

  const register = useCallback(
    async (email: string, displayName: string, password: string, orgName: string) => {
      await api.post<Me>('/api/v1/auth/register', { email, displayName, password, orgName })
      await refresh()
    },
    [refresh],
  )

  // ponytail: sign-out is client-side only — the API has no logout endpoint, so the ed_session cookie
  // stays valid until it expires and anyone with the browser could restore the session by navigating
  // back. Upgrade path: POST /api/v1/auth/logout that clears the cookie (and ideally revokes the JWT),
  // then call it here. Out of scope for a frontend-only task.
  const signOut = useCallback(() => {
    setMe(null)
    setOrg(null)
  }, [])

  return (
    <SessionContext.Provider value={{ me, org, loading, signIn, register, signOut, refresh }}>
      {children}
    </SessionContext.Provider>
  )
}

export function RequireAuth() {
  const { me, loading } = useSession()
  // Rendering the redirect before /me resolves would bounce every signed-in user to /login on reload.
  if (loading) return <p>Loading…</p>
  return me ? <Outlet /> : <Navigate to="/login" replace />
}

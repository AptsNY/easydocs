import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { api, ApiError, type Me, type Org } from './api'

type Session = {
  me: Me | null
  org: Org | null
  loading: boolean
  signIn: (email: string, password: string) => Promise<void>
  register: (email: string, displayName: string, password: string, orgName: string) => Promise<void>
  signOut: () => Promise<void>
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

  // The server clears the httpOnly ed_session cookie; dropping only the in-memory copy left the session
  // restorable with a reload. The local state is cleared even if the call fails — a user who pressed
  // Sign out must never be left looking signed in, and the route guard is what keeps them off the app.
  const signOut = useCallback(async () => {
    try {
      await api.post('/api/v1/auth/logout')
    } catch (e) {
      console.error('logout failed', e)
    }
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
  const location = useLocation()
  // Rendering the redirect before /me resolves would bounce every signed-in user to /login on reload.
  if (loading) return <p>Loading…</p>
  // Carry the destination through the sign-in so a deep link survives it. Without this every bookmarked
  // URL lands on the dashboard, and an invitation link — whose whole payload is in the path — is lost.
  return me ? (
    <Outlet />
  ) : (
    <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  )
}

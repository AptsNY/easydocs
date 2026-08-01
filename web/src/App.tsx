import { useEffect, useState } from 'react'
import { Link, Navigate, Outlet, Route, Routes, useNavigate } from 'react-router'
import { api, type OrgMembership } from './api'
import { RequireAuth, useSession } from './auth'
import AcceptInvitation from './routes/AcceptInvitation'
import Approvals from './routes/Approvals'
import Audit from './routes/Audit'
import Compare from './routes/Compare'
import Copies from './routes/Copies'
import Dashboard from './routes/Dashboard'
import DocumentConsole from './routes/DocumentConsole'
import Editor from './routes/Editor'
import History from './routes/History'
import Login from './routes/Login'
import MajorVersions from './routes/MajorVersions'
import Settings from './routes/Settings'
import ShareLanding from './routes/ShareLanding'

// Every screen spec §9 names gets a route now, even where the component is still a stub, so routing is
// verifiable before the content exists.
export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      {/* Public on purpose: the anonymous share landing sits outside RequireAuth (spec §11). */}
      <Route path="/s/:token" element={<ShareLanding />} />
      <Route element={<RequireAuth />}>
        <Route element={<Shell />}>
          <Route path="/" element={<Dashboard />} />
          <Route path="/folders/:folderId" element={<Dashboard />} />
          <Route path="/trash" element={<Dashboard trashed />} />
          <Route path="/approvals" element={<Approvals inbox />} />
          {/* Inside the guard: accepting binds the invitation to the signed-in identity, so a signed-out
              recipient is sent to /login and returned here with the token still in the path. */}
          <Route path="/invitations/:token" element={<AcceptInvitation />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="/documents/:id" element={<DocumentConsole />}>
            <Route index element={<History />} />
            <Route path="major-versions" element={<MajorVersions />} />
            <Route path="copies" element={<Copies />} />
            <Route path="approvals" element={<Approvals />} />
            <Route path="audit" element={<Audit />} />
          </Route>
          <Route path="/documents/:id/compare" element={<Compare />} />
          <Route path="/versions/:vid/edit" element={<Editor />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

// The app shell: org identity plus the three top-level destinations. A layout route so every
// authenticated screen gets it without repeating the markup.
function Shell() {
  const { org, me, signOut } = useSession()
  const navigate = useNavigate()

  return (
    <div className="shell">
      {/* First stop in the tab order, visible only when focused: the masthead is five links deep on
          every screen, and a keyboard reader should not have to walk it to reach the work. */}
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header>
        <Link to="/" className="brand">
          easydocs
        </Link>
        <span data-testid="org-name">{org?.name}</span>
        <OrgSwitcher />
        <nav>
          <Link to="/">Documents</Link>
          <Link to="/approvals">Approvals</Link>
          <Link to="/settings">Settings</Link>
        </nav>
        <span className="who">{me?.displayName}</span>
        <button
          type="button"
          className="link"
          onClick={() => {
            void signOut().then(() => navigate('/login', { replace: true }))
          }}
        >
          Sign out
        </button>
      </header>
      <main id="main">
        <Outlet />
      </main>
    </div>
  )
}

// Renders nothing for the ordinary single-org user, which is why it is a plain <select> and not a
// screen: someone invited into a colleague's organization is a member of two, and a session binds to
// exactly one. Switching re-issues the session cookie server-side, so everything already on screen is
// now about the wrong org — hence the full reload rather than a refetch of one view.
function OrgSwitcher() {
  const { org } = useSession()
  const [orgs, setOrgs] = useState<OrgMembership[]>([])

  useEffect(() => {
    if (!org) return
    api
      .get<{ items: OrgMembership[] }>('/api/v1/orgs')
      .then((page) => setOrgs(page.items))
      .catch((e: unknown) => console.error('org list failed', e))
  }, [org])

  if (orgs.length < 2) return null

  return (
    <label className="org-switcher">
      <span className="visually-hidden">Organization</span>
      <select
        data-testid="org-switcher"
        value={org?.id ?? ''}
        onChange={(e) => {
          void api
            .post('/api/v1/auth/switch-org', { orgId: e.target.value })
            .then(() => window.location.assign('/'))
        }}
      >
        {orgs.map((o) => (
          <option key={o.id} value={o.id}>
            {o.name}
          </option>
        ))}
      </select>
    </label>
  )
}

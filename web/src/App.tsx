import { Link, Navigate, Outlet, Route, Routes, useNavigate } from 'react-router'
import { RequireAuth, useSession } from './auth'
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
      <header>
        <Link to="/" className="brand">
          easydocs
        </Link>
        <span data-testid="org-name">{org?.name}</span>
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
            signOut()
            navigate('/login', { replace: true })
          }}
        >
          Sign out
        </button>
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  )
}

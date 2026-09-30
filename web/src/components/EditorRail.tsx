import { useState } from 'react'
import { Link } from 'react-router'
import type { Approval, Member, VersionRow } from '../api'
import { useRead } from '../useRead'

const KEY = 'easydocs.editorRail.open'

// Remembered per browser; the first visit starts open unless the window is narrow.
function initialOpen() {
  try {
    const saved = localStorage.getItem(KEY)
    if (saved !== null) return saved === '1'
  } catch {
    // Storage blocked (private mode, policy): fall through to the width default.
  }
  return !window.matchMedia('(max-width: 1099px)').matches
}

type Props = {
  documentId: string
  versionId: string
  versions: { data?: VersionRow[]; failed?: boolean }
  tick: number
}

// Read-only context while editing. Everything that changes something lives on the full pages it links to.
export default function EditorRail({ documentId, versionId, versions, tick }: Props) {
  const [open, setOpen] = useState(initialOpen)
  const members = useRead<Member[]>(`/api/v1/documents/${documentId}/members`, tick)
  const approvals = useRead<Approval[]>(`/api/v1/versions/${versionId}/approvals`, tick)

  const toggle = () => {
    setOpen(!open)
    try {
      localStorage.setItem(KEY, open ? '0' : '1')
    } catch {
      // Not remembered this time; the rail still toggles.
    }
  }

  return (
    <aside className={open ? 'editor-rail' : 'editor-rail editor-rail--closed'} data-testid="editor-rail">
      <button type="button" className="link editor-rail-toggle" aria-expanded={open} aria-controls="editor-rail-body" onClick={toggle}>
        {open ? 'Hide details »' : '« Details'}
      </button>
      <div id="editor-rail-body" hidden={!open}>
        <section>
          <h3>History</h3>
          {versions.failed ? (
            <p className="error">Couldn't load.</p>
          ) : (
            <ul>
              {versions.data?.slice(0, 8).map((v) => (
                <li key={v.id}>
                  <span className="version-number">{v.number}</span> {v.createdByName} ·{' '}
                  {new Date(v.createdAt).toLocaleString()}
                </li>
              ))}
            </ul>
          )}
          <Link to={`/documents/${documentId}`}>Full history</Link>
        </section>

        <section>
          <h3>Members</h3>
          {members.failed ? (
            <p className="error">Couldn't load.</p>
          ) : (
            <ul>
              {members.data?.map((m) => (
                <li key={m.userId}>
                  {m.displayName} ({m.role})
                </li>
              ))}
            </ul>
          )}
        </section>

        <section>
          <h3>Approvals</h3>
          {approvals.failed ? (
            <p className="error">Couldn't load.</p>
          ) : approvals.data?.length ? (
            <ul>
              {approvals.data.map((a) => (
                <li key={a.id}>
                  {a.approverName}: {a.status}
                </li>
              ))}
            </ul>
          ) : (
            <p>None for this version.</p>
          )}
          <Link to={`/documents/${documentId}/approvals`}>All approvals</Link>
        </section>
      </div>
    </aside>
  )
}

import { useCallback, useEffect, useState } from 'react'
import {
  api,
  problemText,
  type DocRole,
  type Member,
  type MemberAdded,
} from '../api'
import { useSession } from '../auth'

const ROLES: DocRole[] = ['Owner', 'Editor', 'Viewer']

// Spec §9's members panel. Membership is strictly per-document: org role grants nothing, so this is the
// only way a second person reaches a document. Any member may read the roster; only an Owner may change
// it, and the API enforces that independently — hiding the controls is courtesy, not security.
export default function MembersPanel({
  documentId,
  tick,
}: {
  documentId: string | undefined
  tick: number
}) {
  const { me } = useSession()
  const [members, setMembers] = useState<Member[]>([])
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<DocRole>('Editor')
  const [invitation, setInvitation] = useState<{ email: string; token: string } | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    if (!documentId) return
    setMembers(await api.get<Member[]>(`/api/v1/documents/${documentId}/members`))
  }, [documentId])

  useEffect(() => {
    load().catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  // Every mutation goes through here, so "Last owner" (409, with the detail that explains it) always
  // reaches the screen. A silent no-op on the last-owner path would look like a broken button.
  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
    } catch (e) {
      setError(problemText(e))
    }
    await load().catch((e: unknown) => setError(problemText(e)))
  }

  const canManage = members.find((m) => m.userId === me?.id)?.role === 'Owner'

  return (
    <aside className="members" data-testid="members-panel" aria-label="Members">
      <h3>Members</h3>

      <ul>
        {members.map((m) => (
          <li key={m.userId} className="member-row" data-testid="member-row" data-email={m.email}>
            <span className="member-who">
              {m.displayName} <span className="muted">{m.email}</span>
            </span>

            {/* ONE role element per row, not two. The role used to be stated twice — as text and as a
                select right beside it — so an owner read "Owner Owner". Where the caller may change
                it the select IS the statement (its selected option is the role, and it carries
                data-testid="member-role" so the roster assertion still has one thing to read); where
                they may not, it is text. Either way the row says the role exactly once. */}
            {canManage ? (
              <label className="member-role">
                <span className="visually-hidden">Change role for {m.email}</span>
                <select
                  data-testid="member-role"
                  value={m.role}
                  onChange={(e) => {
                    const next = e.target.value
                    void act(() =>
                      api.patch(`/api/v1/documents/${documentId}/members/${m.userId}`, {
                        role: next,
                      }),
                    )
                  }}
                >
                  {ROLES.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <span className="member-role" data-testid="member-role">
                {m.role}
              </span>
            )}

            {canManage && (
              // Destructive, so it is not a bare underlined word among links any more: a hairline
              // control that goes to the danger ink on hover and focus.
              <button
                type="button"
                className="link danger"
                aria-label={`Remove ${m.email}`}
                onClick={() =>
                  void act(() => api.del(`/api/v1/documents/${documentId}/members/${m.userId}`))
                }
              >
                Remove
              </button>
            )}
          </li>
        ))}
      </ul>

      {canManage && (
        <details className="disclose" data-testid="add-member">
          <summary>Add someone</summary>
          <form
            className="stack"
            onSubmit={(e) => {
              e.preventDefault()
              if (!email.trim()) return
              void act(async () => {
                const added = await api.post<MemberAdded>(
                  `/api/v1/documents/${documentId}/members`,
                  { email: email.trim(), role },
                )
                // An email already in this org becomes a member outright; anyone else gets an
                // invitation whose raw token the API returns EXACTLY ONCE (it stores only the hash).
                // So this is the one moment it can be shown — nothing re-fetches it, and a reload
                // loses it for good.
                setInvitation(
                  added.invitationToken
                    ? { email: added.email, token: added.invitationToken }
                    : null,
                )
                setEmail('')
              })
            }}
          >
            <label className="visually-hidden" htmlFor="member-email">
              Email
            </label>
            <input
              id="member-email"
              type="email"
              placeholder="Email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />

            <label htmlFor="member-role">Role</label>
            <select
              id="member-role"
              value={role}
              onChange={(e) => setRole(e.target.value as DocRole)}
            >
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>

            <button type="submit">Add member</button>
          </form>
        </details>
      )}

      {invitation && (
        <div className="invitation" role="status">
          <p>
            Invited {invitation.email}. Send them this link now — the token is shown once and cannot be
            recovered.
          </p>
          {/* The link is the useful half: it lands on the screen that redeems the token. The bare token
              stays visible because it is what the API returns and what an automated caller needs. */}
          <code data-testid="invitation-url">{`${window.location.origin}/invitations/${invitation.token}`}</code>
          <code data-testid="invitation-token">{invitation.token}</code>
        </div>
      )}

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </aside>
  )
}

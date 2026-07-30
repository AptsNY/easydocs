import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  api,
  problemText,
  type ApiTokenRow,
  type OrgMember,
  type OrgRole,
  type Org,
} from '../api'
import { useSession } from '../auth'

const ROLES: OrgRole[] = ['Owner', 'Admin', 'Member']

// Settings (spec §9): profile, `ed_` API tokens, org and org members. Before M4.5 there were no org
// endpoints at all — the only way to get a second person into an org was to add them to a document and let
// the invitation drag them in sideways.
//
// The controls are gated on org.myRole, which the API enforces independently (Owner/Admin to rename and
// invite, Owner to change a role or remove). Hiding them is courtesy; the 403s and the last-owner 409 are
// the enforcement, and every one of them is shown verbatim.
export default function Settings() {
  const { me, org, refresh } = useSession()
  const [tokens, setTokens] = useState<ApiTokenRow[]>([])
  const [members, setMembers] = useState<OrgMember[]>([])
  const [minted, setMinted] = useState<{ name: string; token: string } | null>(null)
  const [invitation, setInvitation] = useState<{ email: string; token: string } | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    const [ts, ms] = await Promise.all([
      api.get<ApiTokenRow[]>('/api/v1/tokens'),
      api.get<OrgMember[]>('/api/v1/org/members'),
    ])
    // Revoking is a soft revoke server-side (the row stays, with revokedAt set). A revoked token is dead —
    // it authenticates nothing — so listing it would only invite someone to try it.
    setTokens(ts.filter((t) => t.revokedAt === null))
    setMembers(ms)
  }, [])

  useEffect(() => {
    load().catch((e: unknown) => setError(problemText(e)))
  }, [load])

  // refresh() as well as load(): a rename changes the org name in the shell header, and an owner who
  // demotes themselves must lose these controls on the spot.
  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
    } catch (e) {
      setError(problemText(e))
    }
    await Promise.all([load(), refresh()]).catch((e: unknown) => setError(problemText(e)))
  }

  const canAdmin = org?.myRole === 'Owner' || org?.myRole === 'Admin'
  const isOwner = org?.myRole === 'Owner'

  const createToken = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const name = String(new FormData(form).get('name') ?? '').trim()
    if (!name) return
    void act(async () => {
      // The raw token comes back EXACTLY ONCE — only its hash is stored — so this is the one moment it can
      // be shown. Nothing re-fetches it and a reload loses it for good, which the copy below says plainly.
      const created = await api.post<{ id: string; token: string }>('/api/v1/tokens', {
        name,
        scopes: [],
      })
      setMinted({ name, token: created.token })
      form.reset()
    })
  }

  const rename = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const name = String(new FormData(e.currentTarget).get('name') ?? '').trim()
    if (!name) return
    void act(() => api.patch<Org>('/api/v1/org', { name }))
  }

  const invite = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    const email = String(data.get('email') ?? '').trim()
    if (!email) return
    void act(async () => {
      // Same one-shot rule as a token and a share link: the invitation's raw token is returned once and only
      // its hash is kept, so it has to be handed over now.
      const created = await api.post<{ email: string; invitationToken: string }>(
        '/api/v1/org/members',
        { email, role: String(data.get('role') ?? 'Member') },
      )
      setInvitation({ email: created.email, token: created.invitationToken })
      form.reset()
    })
  }

  return (
    <section className="settings" data-testid="settings">
      <h2>Settings</h2>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      <section>
        <h3>Profile</h3>
        <p>
          <span data-testid="profile-name">{me?.displayName}</span>{' '}
          <span className="muted" data-testid="profile-email">
            {me?.email}
          </span>
        </p>
      </section>

      <section>
        <h3>API tokens</h3>
        {/* ponytail: no scopes picker and no expiry field. Scopes are stored but nothing in the API checks
            them yet, and an unscoped non-expiring token is exactly what the endpoint mints by default — a
            picker here would imply a restriction that does not exist. Add both when scope enforcement lands.
            The list is org-wide, not per-user, because GET /api/v1/tokens is (a Member sees their
            colleagues' token names, never a value). */}
        <p className="muted">Tokens act for this organization. The value is shown once, at creation.</p>

        <form className="stack" onSubmit={createToken}>
          <label htmlFor="token-name">Token name</label>
          <input id="token-name" name="name" required />
          <button type="submit">Create token</button>
        </form>

        {minted && (
          <div className="invitation" role="status">
            <p>
              Copy the token for “{minted.name}” now — it is shown once and cannot be recovered. Create a
              new one if you lose it.
            </p>
            <code data-testid="token-value">{minted.token}</code>
          </div>
        )}

        <ul className="rows">
          {tokens.map((t) => (
            <li key={t.id} data-testid="token-row" data-name={t.serviceName}>
              <span>{t.serviceName}</span>
              <time dateTime={t.createdAt}>{new Date(t.createdAt).toLocaleDateString()}</time>
              <span className="muted">
                {t.lastUsedAt ? `Last used ${new Date(t.lastUsedAt).toLocaleDateString()}` : 'Never used'}
              </span>
              <button
                type="button"
                className="link"
                aria-label={`Revoke ${t.serviceName}`}
                onClick={() => void act(() => api.del(`/api/v1/tokens/${t.id}`))}
              >
                Revoke
              </button>
            </li>
          ))}
        </ul>
        {tokens.length === 0 && <p className="muted">No tokens.</p>}
      </section>

      <section>
        <h3>Organization</h3>
        <p>
          <span data-testid="org-display-name">{org?.name}</span>{' '}
          <code data-testid="org-slug">{org?.slug}</code>
        </p>
        {/* The slug is deliberately not editable: R8 bakes it into every download filename, so re-slugging on
            a rename would silently change what people receive. */}
        <p className="muted">
          The short name appears in download filenames, so it stays fixed when the organization is renamed.
        </p>

        {canAdmin && (
          <form className="stack" data-testid="org-rename" onSubmit={rename}>
            <label htmlFor="org-name-input">Organization name</label>
            <input id="org-name-input" name="name" defaultValue={org?.name ?? ''} />
            <button type="submit">Rename</button>
          </form>
        )}
      </section>

      <section>
        <h3>Members</h3>
        {/* Org membership is not document access: it grants nothing on any document (spec §11). It is what
            makes someone available to be added to one, and to be named an approver. */}
        <p className="muted">
          Organization roles govern this screen only. Access to a document is granted per document.
        </p>

        <ul className="rows">
          {members.map((m) => (
            <li key={m.userId} data-testid="org-member-row" data-email={m.email}>
              <span>
                {m.displayName} <span className="muted">{m.email}</span>
              </span>
              <span data-testid="org-member-role">{m.role}</span>

              {isOwner && (
                <>
                  <label>
                    <span className="visually-hidden">Change organization role for {m.email}</span>
                    <select
                      data-testid="org-member-role-select"
                      value={m.role}
                      onChange={(e) => {
                        const role = e.target.value
                        void act(() => api.patch(`/api/v1/org/members/${m.userId}`, { role }))
                      }}
                    >
                      {ROLES.map((r) => (
                        <option key={r} value={r}>
                          {r}
                        </option>
                      ))}
                    </select>
                  </label>

                  <button
                    type="button"
                    className="link"
                    aria-label={`Remove ${m.email} from the organization`}
                    onClick={() => void act(() => api.del(`/api/v1/org/members/${m.userId}`))}
                  >
                    Remove
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>

        {canAdmin && (
          <form className="stack" data-testid="org-invite" onSubmit={invite}>
            <label htmlFor="invite-email">Invite by email</label>
            <input id="invite-email" name="email" type="email" />

            <label htmlFor="invite-role">Role</label>
            <select id="invite-role" name="role" defaultValue="Member">
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>

            <button type="submit">Invite</button>
          </form>
        )}

        {invitation && (
          <div className="invitation" role="status">
            <p>
              Invited {invitation.email}. Send them this invitation token now — it is shown once and cannot
              be recovered.
            </p>
            <code data-testid="org-invitation-token">{invitation.token}</code>
          </div>
        )}
      </section>
    </section>
  )
}

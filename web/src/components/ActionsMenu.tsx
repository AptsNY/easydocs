import { useId, useRef, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import {
  api,
  problemText,
  type DocRole,
  type Paged,
  type ShareLinkRow,
  type VersionRow as Version,
} from '../api'
import { useSession } from '../auth'
import Modal from './Modal'

// The v1 action set from spec §9, conformance criterion E8: "Open in Collabora, Import, Share, Download,
// Name, Publish, Revert, Push To Copy (8 actions; desktop 'Open in Word' and Export are v1.1)."
//
// The actions live in ONE array with ONE role predicate rather than eight scattered `&&`s, because that
// single filter is what makes "a Viewer sees only the read-only actions" a property of the menu instead of
// a coincidence of eight independent conditions. Hiding is courtesy, not security — every endpoint
// re-checks the role server-side — but a Viewer should not be shown a button that can only ever 403.
type Need = 'read' | 'edit' | 'own'
type ModalKind = 'share' | 'name' | 'publish' | 'copy'

const TITLES: Record<ModalKind, string> = {
  share: 'Share a read-only link',
  name: 'Name this version',
  publish: 'Publish this version',
  copy: 'Push to a new copy',
}

export default function ActionsMenu({
  version,
  documentId,
  role,
  onDone,
}: {
  version: Version
  documentId: string
  role: DocRole
  onDone: () => void
}) {
  const navigate = useNavigate()
  const { me } = useSession()
  const [open, setOpen] = useState(false)
  const [modal, setModal] = useState<ModalKind | null>(null)
  const [shareUrl, setShareUrl] = useState('')
  const [links, setLinks] = useState<ShareLinkRow[]>([])
  const [error, setError] = useState('')
  const triggerRef = useRef<HTMLButtonElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const listId = useId()
  const fieldId = useId()

  const closeMenu = () => {
    setOpen(false)
    triggerRef.current?.focus()
  }
  // The share URL is cleared here on purpose: the API returns the raw token exactly once, so reopening the
  // dialog must not re-show a link the server can no longer produce.
  const closeModal = () => {
    setModal(null)
    setShareUrl('')
    setLinks([])
    setError('')
    triggerRef.current?.focus()
  }

  // The links the document already has. Document-scoped, not version-scoped, because that is the question
  // — "what have I shared?" — so each row names the version it points at. Not routed through act(): opening
  // the dialog is a read, and act() also fires onDone(), which would refetch the whole version list.
  const loadLinks = async () => {
    try {
      const page = await api.get<Paged<ShareLinkRow>>(`/api/v1/documents/${documentId}/share-links`)
      setLinks(page.items)
    } catch (e) {
      setError(problemText(e))
    }
  }

  // Revoked and expired rows are listed too, flagged — a dead link is part of the answer, and the API
  // treats both as the same 404 for the recipient. Only a live one gets a Revoke button.
  const linkState = (l: ShareLinkRow) =>
    l.revokedAt ? 'Revoked' : l.expiresAt && new Date(l.expiresAt) <= new Date() ? 'Expired' : 'Live'

  // Every action funnels through here, so a failure always reaches a role="alert" with the API's own
  // words. A silent no-op is worse than an error message. `stay` is for Share, whose result IS the dialog.
  const act = async (fn: () => Promise<unknown>, andThen: 'close' | 'stay' = 'close') => {
    try {
      setError('')
      await fn()
      if (andThen === 'close') closeModal()
      onDone()
    } catch (e) {
      setError(problemText(e))
    }
  }

  // FormData is read synchronously: `currentTarget` is only valid for the duration of the dispatch.
  const onSubmit = (fn: (f: FormData) => Promise<unknown>, andThen?: 'close' | 'stay') =>
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault()
      const data = new FormData(e.currentTarget)
      void act(() => fn(data), andThen)
    }

  const vid = version.id
  const actions: { label: string; need: Need; run: () => void }[] = [
    // The session is minted by the editor route, not here: arriving at /versions/:vid/edit by any means —
    // a bookmark, a reload, a link — has to mint one anyway.
    { label: 'Open in Collabora', need: 'edit', run: () => navigate(`/versions/${vid}/edit`) },
    {
      // Desktop Word via ms-word: + WebDAV (issue #11). The mint returns a protocol URL; assigning
      // it hands off to the registered Office handler and never navigates the SPA. On a machine
      // with no Word the browser shows its "no handler" notice — the honest outcome.
      label: 'Open in Word',
      need: 'edit',
      run: () =>
        void act(async () => {
          const s = await api.post<{ msWordUrl: string }>(`/api/v1/versions/${vid}/webdav-sessions`)
          window.location.assign(s.msWordUrl)
        }),
    },
    { label: 'Import', need: 'edit', run: () => fileRef.current?.click() },
    {
      label: 'Share',
      need: 'read',
      run: () => {
        setModal('share')
        void loadLinks()
      },
    },
    {
      label: 'Download',
      need: 'read',
      // ponytail: a location assign rather than a synthetic <a download> click — the response carries
      // Content-Disposition: attachment, so the browser downloads and never navigates. Ceiling: a 403/404
      // here WOULD navigate, to a problem+json document. Upgrade path if that becomes reachable (it needs
      // the row to outlive the version): fetch it and objectURL the blob so the failure stays in-page.
      run: () => window.location.assign(`/api/v1/versions/${vid}/download`),
    },
    { label: 'Name', need: 'edit', run: () => setModal('name') },
    { label: 'Publish', need: 'edit', run: () => setModal('publish') },
    // No confirmation: a revert commits the target's bytes as a NEW head and touches no existing version
    // (E11), so there is nothing to lose and nothing to confirm.
    { label: 'Revert', need: 'edit', run: () => void act(() => api.post(`/api/v1/versions/${vid}/revert`)) },
    { label: 'Push To Copy', need: 'edit', run: () => setModal('copy') },
  ]

  const canEdit = role === 'Owner' || role === 'Editor'
  // ponytail: no v1 action needs 'own' — the eight are read or edit. It is in the union because the rung
  // exists in the domain (DocRole has three values) and Task 16's approval actions are owner-gated, so
  // the alternative is widening this predicate later in two places instead of none.
  const allowed = (need: Need) => need === 'read' || (need === 'own' ? role === 'Owner' : canEdit)

  const alert = error ? (
    <p role="alert" className="error">
      {error}
    </p>
  ) : null

  return (
    <div
      className="actions"
      onKeyDown={(e) => {
        // While a modal is open the dialog owns Escape (it dismisses itself and calls closeModal).
        if (e.key === 'Escape' && !modal) closeMenu()
      }}
    >
      <button
        ref={triggerRef}
        type="button"
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen((o) => !o)}
      >
        Actions
        {/* One trigger per row, so the accessible name has to say which row. */}
        <span className="visually-hidden"> for version {version.number}</span>
      </button>

      {/* ponytail: a disclosure (aria-expanded + a list of buttons), not role="menu". Tab already walks
          the items and Escape closes it, whereas role="menu" would obligate arrow-key roving tabindex for
          no gain on an eight-item list. Upgrade path if the set grows submenus: the full ARIA menu pattern.

          ponytail: no light dismiss — clicking the page background leaves the menu open (Escape, the
          trigger, or picking an action all close it). Deliberate: the obvious one-liner is an onBlur
          relatedTarget check, and that one is a 3am bug, because Safari and Firefox do not focus a button
          on mousedown, so the menu would close before the click it was closing for could land. Upgrade
          path: a document pointerdown listener in an effect, or the native popover attribute once CSS
          anchor positioning is not Chromium-only. */}
      {open && (
        <ul className="actions-menu" id={listId} data-testid="actions-menu">
          {actions
            .filter((a) => allowed(a.need))
            .map((a) => (
              <li key={a.label}>
                <button
                  type="button"
                  className="link"
                  onClick={() => {
                    closeMenu()
                    a.run()
                  }}
                >
                  {a.label}
                </button>
              </li>
            ))}
        </ul>
      )}

      {/* Import's real control is the menu item above; this is the file picker it opens. Out of the a11y
          tree and out of the tab order so it is not a second, nameless "Choose file" stop. */}
      <input
        ref={fileRef}
        type="file"
        accept=".docx"
        className="visually-hidden"
        tabIndex={-1}
        aria-hidden="true"
        onChange={(e) => {
          const file = e.target.files?.[0]
          e.target.value = '' // so importing the same file twice fires change twice
          if (!file) return
          const body = new FormData()
          body.set('file', file)
          // Note the colon: `versions:import` is a separate action from `versions` (upload), and only it
          // stamps the version's source as Import.
          void act(() => api.post(`/api/v1/documents/${documentId}/versions:import`, body))
        }}
      />

      {modal && (
        <Modal title={`${TITLES[modal]} (${version.number})`} onClose={closeModal}>
          {modal === 'share' && (
            <>
              {shareUrl ? (
                <div className="stack">
                  <p>
                    Copy this link now — the API returns the token once and stores only its hash, so it
                    cannot be shown again.
                  </p>
                  <code data-testid="share-url">{shareUrl}</code>
                </div>
              ) : (
                <form
                  className="stack"
                  onSubmit={onSubmit(async (f) => {
                    const raw = String(f.get('expiresAt') ?? '')
                    const link = await api.post<{ token: string; url: string }>(
                      `/api/v1/versions/${vid}/share-links`,
                      { expiresAt: raw ? new Date(raw).toISOString() : null },
                    )
                    setShareUrl(link.url)
                    await loadLinks()
                  }, 'stay')}
                >
                  <label htmlFor={`${fieldId}-expires`}>Expires (optional)</label>
                  <input id={`${fieldId}-expires`} name="expiresAt" type="datetime-local" />
                  <button type="submit">Create link</button>
                </form>
              )}

              {/* Withdrawing a share is the point of this list: until M5 the row id was never exposed, so
                  DELETE /api/v1/share-links/{id} existed and no client could call it. */}
              <h4>Links for this document</h4>
              {links.length === 0 ? (
                <p className="muted">No links yet.</p>
              ) : (
                <ul className="rows">
                  {links.map((l) => (
                    <li key={l.id} data-testid="share-link-row" data-state={linkState(l)}>
                      <span className="version-number">{l.versionNumber}</span>
                      <span>{linkState(l)}</span>
                      <span className="muted">
                        {l.createdByName} · {new Date(l.createdAt).toLocaleDateString()} ·{' '}
                        {l.viewCount} views
                        {l.expiresAt && ` · expires ${new Date(l.expiresAt).toLocaleDateString()}`}
                      </span>
                      {/* Revoke is creator-or-Editor+ server-side, so a Viewer looking at a colleague's
                          link would only ever get a 403 — the same reason the menu itself filters on role.
                          The API is still the enforcement. */}
                      {linkState(l) === 'Live' && (l.createdBy === me?.id || canEdit) && (
                        <button
                          type="button"
                          className="link"
                          aria-label={`Revoke the share link for version ${l.versionNumber} created ${new Date(l.createdAt).toLocaleDateString()}`}
                          onClick={() =>
                            void act(async () => {
                              await api.del(`/api/v1/share-links/${l.id}`)
                              await loadLinks()
                            }, 'stay')
                          }
                        >
                          Revoke
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}

          {modal === 'name' && (
            <form
              className="stack"
              onSubmit={onSubmit((f) =>
                api.patch(`/api/v1/versions/${vid}`, { name: String(f.get('name') ?? '') }),
              )}
            >
              <label htmlFor={`${fieldId}-name`}>Version name</label>
              <input id={`${fieldId}-name`} name="name" defaultValue={version.name ?? ''} autoFocus />
              <button type="submit">Save</button>
            </form>
          )}

          {modal === 'publish' && (
            <form
              className="stack"
              onSubmit={onSubmit((f) =>
                api.post(`/api/v1/versions/${vid}/publish`, {
                  kind: String(f.get('kind') ?? 'minor'),
                  name: String(f.get('name') ?? '') || null,
                }),
              )}
            >
              <p className="muted">
                Publishing renumbers this version from the document counter and adds it to Major
                Versions.
              </p>
              <label htmlFor={`${fieldId}-kind`}>Kind</label>
              <select id={`${fieldId}-kind`} name="kind" defaultValue="minor">
                <option value="minor">minor</option>
                <option value="major">major</option>
              </select>
              <label htmlFor={`${fieldId}-publish-name`}>Publish name</label>
              <input id={`${fieldId}-publish-name`} name="name" />
              <button type="submit">Publish</button>
            </form>
          )}

          {modal === 'copy' && (
            <form
              className="stack"
              onSubmit={onSubmit((f) =>
                api.post(`/api/v1/versions/${vid}/copies`, { name: String(f.get('name') ?? '') || null }),
              )}
            >
              <p className="muted">
                A copy is an isolated document with its own members and its own history — reviewers of the
                copy never see this document.
              </p>
              <label htmlFor={`${fieldId}-copy`}>Copy name</label>
              <input id={`${fieldId}-copy`} name="name" autoFocus />
              <button type="submit">Create copy</button>
            </form>
          )}

          {alert}
          <button type="button" className="link" onClick={closeModal}>
            Close
          </button>
        </Modal>
      )}

      {!modal && alert}
    </div>
  )
}

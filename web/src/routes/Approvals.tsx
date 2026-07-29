import { useCallback, useEffect, useId, useState, type FormEvent } from 'react'
import { Link, useOutletContext, useParams, useSearchParams } from 'react-router'
import {
  api,
  problemText,
  type Approval,
  type DocRole,
  type Member,
  type Paged,
  type VersionRow as Version,
} from '../api'
import { useSession } from '../auth'

// Approvals (spec §9, conformance E7). One component, two mounts, because the row is the same row:
//
//   * /documents/:id/approvals — a console tab: everything asked on this document, plus the request form.
//   * /approvals — the standalone inbox: `filter=assigned` is "asked of me", `requested` is "asked by me".
//
// E7 in the UI: the request form exists only when a PUBLISHED version exists (an approval on a draft is not
// a thing the product can do, so there is nothing to disable), the approver picker lists DOCUMENT MEMBERS
// ONLY, and a closed row offers no second decision — the API's 409 "Already closed" is the enforcement, this
// is just not lying about what is possible.
export default function Approvals({ inbox = false }: { inbox?: boolean }) {
  const { id } = useParams()
  const { me } = useSession()
  // The console passes tick + the caller's document role through the outlet context. At /approvals there is
  // no console above this route, so there is no context — and no document-scoped SSE stream either.
  const outlet = useOutletContext<{ tick: number; myRole: DocRole | null } | null>()
  const tick = outlet?.tick ?? 0
  const myRole = outlet?.myRole ?? null

  const [params, setParams] = useSearchParams()
  const [rows, setRows] = useState<Approval[]>([])
  const [versions, setVersions] = useState<Version[]>([])
  const [members, setMembers] = useState<Member[]>([])
  const [error, setError] = useState('')
  const fieldId = useId()

  // Normalised, not passed through: both are validated server-side and an unrecognised value is a 400, so a
  // hand-typed URL would otherwise break the screen instead of showing the default view.
  const filter = params.get('filter') === 'requested' ? 'requested' : 'assigned'
  const raw = params.get('status')
  const status = raw === 'open' || raw === 'closed' ? raw : ''

  // The URL is the state, so a filtered view is linkable and the back button works. Copied rather than
  // mutated in place: the hook hands back a live instance, and editing it does not re-render on its own.
  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next)
  }

  const load = useCallback(async () => {
    if (inbox) {
      // ponytail: one page, no "Load more" — 100 is the API's MaxLimit, so an inbox with more than 100
      // matching rows shows the newest 100. An approvals worklist that deep is a different screen anyway
      // (that is what the status filter is for). Upgrade path: keep nextCursor and add the button the
      // Major Versions tab already has.
      const q = new URLSearchParams({ filter, limit: '100' })
      if (status) q.set('status', status)
      setRows((await api.get<Paged<Approval>>(`/api/v1/approvals?${q}`)).items)
      return
    }

    const [page, roster] = await Promise.all([
      api.get<Paged<Version>>(`/api/v1/documents/${id}/versions?order=desc&limit=100`),
      api.get<Member[]>(`/api/v1/documents/${id}/members`),
    ])
    setVersions(page.items)
    setMembers(roster)

    // There is no per-document approvals route, so this fans out over the published versions — the only
    // ones that can carry an approval at all.
    //
    // ponytail: one GET per published version in the newest-100 window. Ceiling: a document with dozens of
    // publications makes dozens of cheap indexed reads, and an approval on a publication older than that
    // window is not listed. Upgrade path: GET /documents/{id}/approvals, then this is a single read.
    const published = page.items.filter((v) => v.publishedKind !== null)
    const perVersion = await Promise.all(
      published.map((v) => api.get<Approval[]>(`/api/v1/versions/${v.id}/approvals`)),
    )
    setRows(perVersion.flat().sort((a, b) => b.createdAt.localeCompare(a.createdAt)))
  }, [inbox, filter, status, id])

  useEffect(() => {
    load().catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  // Every mutation goes through here so the API's own sentence reaches the screen — 409 "Already closed" in
  // particular, which is the whole of E7's immutability rule stated in words.
  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
    } catch (e) {
      setError(problemText(e))
    }
    await load().catch((e: unknown) => setError(problemText(e)))
  }

  const canEdit = myRole === 'Owner' || myRole === 'Editor'
  const published = versions.filter((v) => v.publishedKind !== null)

  const request = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    const versionId = String(data.get('versionId') ?? '')
    const approverIds = data.getAll('approverIds').map(String)
    const due = String(data.get('dueAt') ?? '')
    if (!versionId || approverIds.length === 0) {
      setError('Pick a version and at least one approver.')
      return
    }
    void act(async () => {
      await api.post(`/api/v1/versions/${versionId}/approvals`, {
        approverIds,
        // A date input gives 'YYYY-MM-DD', which the API's DateTimeOffset binder rejects outright — it wants
        // a full instant. Parsed as UTC midnight, which is also how the due date is rendered back.
        dueAt: due ? new Date(due).toISOString() : null,
      })
      form.reset()
    })
  }

  const decide = (approvalId: string, decision: 'approved' | 'rejected', form: HTMLFormElement | null) => {
    const comment = form ? String(new FormData(form).get('comment') ?? '').trim() : ''
    void act(() =>
      api.post(`/api/v1/approvals/${approvalId}:respond`, { decision, comment: comment || null }),
    )
  }

  return (
    <div data-testid={inbox ? 'approvals-inbox' : 'approvals'}>
      <h3>{inbox ? 'My approvals' : 'Approvals'}</h3>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {inbox && (
        <div className="compare-pickers">
          {/* Both directions, because both are real work: an approver needs to know what is waiting on them,
              a requester needs to track what they asked for. The URL carries them so a view is linkable. */}
          <span className="compare-picker">
            <label htmlFor={`${fieldId}-filter`}>Show</label>
            <select
              id={`${fieldId}-filter`}
              value={filter}
              onChange={(e) => setParam('filter', e.target.value)}
            >
              <option value="assigned">Asked of me</option>
              <option value="requested">Asked by me</option>
            </select>
          </span>
          <span className="compare-picker">
            <label htmlFor={`${fieldId}-status`}>Status</label>
            <select
              id={`${fieldId}-status`}
              value={status}
              onChange={(e) => setParam('status', e.target.value)}
            >
              <option value="">All</option>
              <option value="open">Open</option>
              <option value="closed">Closed</option>
            </select>
          </span>
        </div>
      )}

      {/* E7: absent, not disabled, until something is published. */}
      {!inbox && canEdit && published.length > 0 && (
        <form className="stack request-approval" data-testid="request-approval" onSubmit={request}>
          <h4>Request approval</h4>

          <label htmlFor={`${fieldId}-version`}>Version</label>
          {/* Published only. The API refuses a draft, and it is right to: an approval names a version people
              outside the editing loop are meant to read. No defaultValue — the list arrives after mount and
              a select with nothing selected takes its first option, the newest publication. */}
          <select id={`${fieldId}-version`} name="versionId">
            {published.map((v) => (
              <option key={v.id} value={v.id}>
                {v.number}
              </option>
            ))}
          </select>

          <fieldset className="approvers">
            <legend>Approvers</legend>
            {/* Document members only. The API rejects an approverId that is not a member of this document —
                being named approver is a decision right, and handing one over a document the person cannot
                read is the §11 hole Phase A closed. Listing the org roster here would make that 400 reachable
                by clicking; listing members makes it unreachable. */}
            <ul>
              {members.map((m) => (
                <li key={m.userId} data-testid="approver-option" data-email={m.email}>
                  <label>
                    <input type="checkbox" name="approverIds" value={m.userId} />{' '}
                    {m.displayName} <span className="muted">{m.email}</span>
                  </label>
                </li>
              ))}
            </ul>
          </fieldset>

          <label htmlFor={`${fieldId}-due`}>Due date</label>
          <input id={`${fieldId}-due`} name="dueAt" type="date" />

          <button type="submit">Request approval</button>
        </form>
      )}

      <ul className="rows">
        {rows.map((a) => (
          <li key={a.id} data-testid="approval-row" data-approval-id={a.id} data-status={a.status}>
            {/* In the inbox the document is somewhere else, so it is a link; on the document's own tab it is
                just the name. Both come off the row — no request per item. */}
            {inbox ? (
              <Link to={`/documents/${a.documentId}/approvals`} data-testid="approval-document">
                {a.documentName}
              </Link>
            ) : (
              <span data-testid="approval-document">{a.documentName}</span>
            )}
            <span className="version-number" data-testid="approval-version">
              {a.versionNumber}
            </span>
            <span data-testid="approval-status" className="badge">
              {a.status}
            </span>
            <span className="muted">
              {a.requestedByName} asked <span data-testid="approval-approver">{a.approverName}</span>
            </span>
            {a.dueAt && (
              // A due date is a date, not an instant: rendering the UTC calendar day it was stored as keeps
              // it off by nothing, where toLocaleDateString would show the day before west of Greenwich.
              <time data-testid="approval-due" dateTime={a.dueAt}>
                Due {a.dueAt.slice(0, 10)}
              </time>
            )}
            {a.decisionComment && (
              <span data-testid="approval-comment">{a.decisionComment}</span>
            )}

            {a.status === 'open' && a.approverId === me?.id && (
              // A form purely for its FormData; both buttons are type="button" so that Enter in the comment
              // field cannot implicitly submit — an approval is not something to trip into.
              <form className="approval-decision" onSubmit={(e) => e.preventDefault()}>
                <label htmlFor={`${fieldId}-comment-${a.id}`}>Comment</label>
                <input id={`${fieldId}-comment-${a.id}`} name="comment" />
                {/* Several rows can be open at once, so each button says which one it decides. */}
                <button type="button" onClick={(e) => decide(a.id, 'approved', e.currentTarget.form)}>
                  Approve
                  <span className="visually-hidden">
                    {' '}
                    {a.documentName} {a.versionNumber}
                  </span>
                </button>
                <button type="button" onClick={(e) => decide(a.id, 'rejected', e.currentTarget.form)}>
                  Reject
                  <span className="visually-hidden">
                    {' '}
                    {a.documentName} {a.versionNumber}
                  </span>
                </button>
              </form>
            )}

            {/* The requester may withdraw what they asked for; a document editor may clear anyone's. */}
            {a.status === 'open' && (a.requestedBy === me?.id || canEdit) && (
              <button
                type="button"
                className="link"
                onClick={() => void act(() => api.post(`/api/v1/approvals/${a.id}:cancel`))}
              >
                Cancel
                <span className="visually-hidden">
                  {' '}
                  the approval asked of {a.approverName} on {a.versionNumber}
                </span>
              </button>
            )}
          </li>
        ))}
      </ul>

      {rows.length === 0 && !error && (
        <p className="muted">
          {inbox
            ? 'Nothing here. Approvals you are asked for, and the ones you ask for, both show up on this screen.'
            : 'No approvals on this document yet. Approvals are requested on a published version.'}
        </p>
      )}
    </div>
  )
}

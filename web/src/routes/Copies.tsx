import { useCallback, useEffect, useId, useState, type FormEvent } from 'react'
import { Link, useOutletContext, useParams } from 'react-router'
import {
  api,
  problemText,
  type Copy,
  type DocRole,
  type Paged,
  type PushRequest,
  type VersionRow as Version,
} from '../api'

// Copies & push-back (spec §8, §9, conformance E9) — the signature feature's management screen. It serves
// both ends of the relationship, because a copy is an ordinary document and this is its Copies tab too:
//
//   * on a master: the copies forked out of it, and the push requests coming back in, with Accept/Reject.
//   * on a copy: a control to send one of its versions back, and the fate of the ones already sent.
//
// Accepting materialises the pushed content as an IncomingPush branch on this document's history (which
// the History tab already renders); rejecting means it never enters the history at all.
export default function Copies() {
  const { id } = useParams()
  const { tick, myRole } = useOutletContext<{ tick: number; myRole: DocRole | null }>()
  const [copies, setCopies] = useState<Copy[]>([])
  const [requests, setRequests] = useState<PushRequest[]>([])
  const [versions, setVersions] = useState<Version[]>([])
  const [error, setError] = useState('')
  const fieldId = useId()

  const load = useCallback(async () => {
    const [cs, rs, vs] = await Promise.all([
      api.get<Copy[]>(`/api/v1/documents/${id}/copies`),
      api.get<PushRequest[]>(`/api/v1/documents/${id}/push-requests`),
      api.get<Paged<Version>>(`/api/v1/documents/${id}/versions?order=desc`),
    ])
    setCopies(cs)
    setRequests(rs)
    setVersions(vs.items)
  }, [id])

  useEffect(() => {
    load().catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  // Accept/reject publish `push.reviewed` to the COPY (the pusher may hold no role here), so the target's
  // own stream does not carry the decision — this screen has to re-read after acting rather than waiting
  // for a tick.
  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
    } catch (e) {
      setError(problemText(e))
    }
    await load().catch((e: unknown) => setError(problemText(e)))
  }

  // Is THIS document a copy? GET /documents/{id} does not carry parentDocumentId, but the fork path is
  // visible in the history: a copy's very first version is committed with source CopyPush and no parent
  // (an incoming push always parents its fork point, and an ordinary first version is an Upload/Import).
  // The push endpoint defaults targetDocumentId to the document this copy was forked from, so knowing
  // "yes, a copy" is all this screen needs — it never has to name the parent.
  //
  // ponytail: the inference needs that first version on the loaded page (25 newest). Ceiling: on a copy
  // with more history than that, the send-back control disappears. Upgrade path: add parentDocumentId to
  // the document detail projection, then this is one field test.
  const isCopy = versions.some(
    (v) => v.source === 'CopyPush' && v.parentVersionId === null && v.branchKind === 'Main',
  )
  const canEdit = myRole === 'Owner' || myRole === 'Editor'
  const copyName = (copyId: string) => copies.find((c) => c.id === copyId)?.name

  const sendBack = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const versionId = String(new FormData(e.currentTarget).get('versionId') ?? '')
    // targetDocumentId is deliberately omitted: the API defaults it to the document this copy was forked
    // from, and that is the ONLY target it will accept (the one sanctioned bypass is scoped to it).
    void act(() => api.post(`/api/v1/documents/${id}/pushes`, { versionId }))
  }

  return (
    <div data-testid="copies">
      <h3>Copies</h3>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      <ul className="rows">
        {copies.map((c) => (
          <li key={c.id} data-testid="copy-row" data-copy-id={c.id}>
            {/* A copy is a document of its own, with its own members and history, so its name is a link to
                its own console rather than something expandable here. */}
            <Link to={`/documents/${c.id}`}>{c.name}</Link>
            <time dateTime={c.createdAt}>{new Date(c.createdAt).toLocaleString()}</time>
          </li>
        ))}
      </ul>

      {copies.length === 0 && (
        <p className="muted">
          No copies yet. “Push To Copy” in a version’s Actions menu forks one — reviewers of a copy never
          see this document.
        </p>
      )}

      {isCopy && canEdit && (
        <form className="stack push-back" onSubmit={sendBack}>
          <h4>Send a version back to the original</h4>
          <p className="muted">
            The original’s editors review what you send; until they accept it, nothing of yours reaches
            their history.
          </p>
          <label htmlFor={`${fieldId}-version`}>Version to send back</label>
          {/* No defaultValue: the versions arrive after mount, and a select with no selected option takes
              its first one — which is the newest version, the one a reviewer means. */}
          <select id={`${fieldId}-version`} name="versionId">
            {versions.map((v) => (
              <option key={v.id} value={v.id}>
                {v.number}
              </option>
            ))}
          </select>
          <button type="submit">Send back</button>
        </form>
      )}

      {requests.length > 0 && <h4>Pushes</h4>}
      <ul className="rows">
        {requests.map((r) => {
          const inbound = r.targetDocumentId === id
          return (
            <li key={r.id} data-testid="push-request-row" data-status={r.status}>
              {/* Named by the copy it came from, not by the pusher: the reviewers of this document are not
                  members of the copy, so its version numbers and its people are not theirs to read — and
                  the API gives pushedBy as a bare id anyway. */}
              <span>
                {inbound
                  ? `From ${copyName(r.copyDocumentId) ?? 'a copy'}`
                  : 'Sent back to the original'}
              </span>
              <span className="badge">{r.status.replace('_', ' ')}</span>
              <time dateTime={r.createdAt}>{new Date(r.createdAt).toLocaleString()}</time>

              {inbound && r.status === 'pending' && canEdit && (
                <>
                  {/* Accepting lands the content on an incoming branch — it never overwrites main, so
                      there is nothing to confirm. Rejecting keeps it out of the history entirely and
                      notifies the pusher on their copy. */}
                  {/* A queue can hold several pending pushes, so each button says which one it decides. */}
                  <button
                    type="button"
                    onClick={() => void act(() => api.post(`/api/v1/push-requests/${r.id}:accept`))}
                  >
                    Accept
                    <span className="visually-hidden">
                      {' '}
                      the push from {copyName(r.copyDocumentId) ?? 'a copy'}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void act(() => api.post(`/api/v1/push-requests/${r.id}:reject`))}
                  >
                    Reject
                    <span className="visually-hidden">
                      {' '}
                      the push from {copyName(r.copyDocumentId) ?? 'a copy'}
                    </span>
                  </button>
                </>
              )}
            </li>
          )
        })}
      </ul>
    </div>
  )
}

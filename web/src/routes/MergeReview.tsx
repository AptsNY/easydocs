import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  api,
  getRaw,
  problemText,
  type ChangeSummary,
  type MergePreview,
  type MergeSideRow,
} from '../api'

// The three-way review a Merge click routes through (spec: 2026-08-24-three-way-merge-review-design.md).
// Read-only: nothing here commits until the Merge button's own POST. The preview degrades leg by leg
// (see the MergePreview comment in api.ts), so this screen renders the same shape whichever legs came
// back and never invents a count or a redline for one that did not.

// Mirrors Compare.tsx's sentinel exactly, for the same reason: WmlComparer answers 200 text/html with
// this body when a pair cannot be compared, so there is no status code to branch on.
const UNAVAILABLE = '<p>Comparison unavailable.</p>'

const REDLINE_STYLE =
  '<style>ins{color:#b3261e;text-decoration:underline}del{color:#b3261e;text-decoration:line-through}</style>'

type Redlines = { main: string | null; incoming: string | null }

export default function MergeReview() {
  const { id } = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const left = params.get('left') ?? ''
  const right = params.get('right') ?? ''

  const [preview, setPreview] = useState<MergePreview | null>(null)
  const [redlines, setRedlines] = useState<Redlines>({ main: null, incoming: null })
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [merging, setMerging] = useState(false)

  // The preview itself. Not gated on `left && right` failing silently — the e2e route table (Task 8)
  // visits this path with neither query param present, and `merge-review` still has to render.
  useEffect(() => {
    if (!id || !left || !right) return
    let live = true
    setBusy(true)
    api
      .get<MergePreview>(`/api/v1/documents/${id}/merges/preview?left=${left}&right=${right}`)
      .then(
        (p) => {
          if (!live) return
          setError('')
          setPreview(p)
        },
        (e: unknown) => {
          if (!live) return
          setError(problemText(e, 'Could not load the merge preview.'))
          setPreview(null)
        },
      )
      .finally(() => {
        if (live) setBusy(false)
      })
    return () => {
      live = false
    }
  }, [id, left, right])

  // The two base-relative redlines, fetched only when a fork point exists — a separate effect so a slow
  // or failing redline never withholds the counts and the overlap hint that already arrived above.
  useEffect(() => {
    const base = preview?.base
    if (!id || !base) {
      setRedlines({ main: null, incoming: null })
      return
    }
    let live = true
    const leg = (sideId: string) =>
      getRaw(`/api/v1/documents/${id}/compare?from=${base.id}&to=${sideId}&format=html`)
        .then((r) => r.text())
        // A redline leg degrades on its own too: the counts and the hint already came from the preview
        // response, so a failed render here costs one panel, not the screen.
        .catch(() => UNAVAILABLE)
    Promise.all([leg(preview.main.id), leg(preview.incoming.id)]).then(([main, incoming]) => {
      if (live) setRedlines({ main, incoming })
    })
    return () => {
      live = false
    }
  }, [id, preview?.base, preview?.main.id, preview?.incoming.id])

  const doMerge = () => {
    if (!id || !left || !right) return
    setMerging(true)
    setError('')
    api.post(`/api/v1/documents/${id}/merges`, { left, right }).then(
      () => navigate(`/documents/${id}`),
      (e: unknown) => {
        setError(problemText(e, 'Could not complete the merge.'))
        setMerging(false)
      },
    )
  }

  return (
    <section data-testid="merge-review">
      <h2>Review this merge</h2>

      {/* Outside the `preview &&` block on purpose, like Compare.tsx's own back link. Cancel lives in
          the actions row, which only renders once a preview arrived — so without this, a 403, a 404, or
          the documented 409 (neither side on an incoming branch) would leave the reader on an error
          message with no way back to the document but the browser's own button. */}
      <p>
        <Link to={`/documents/${id}`}>Back to the document</Link>
      </p>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {busy && !preview && <p>Loading the merge preview…</p>}

      {preview && (
        <>
          {preview.base ? (
            <p data-testid="merge-base">
              Both versions started from version <code>{preview.base.number}</code>.
            </p>
          ) : (
            <p data-testid="merge-no-base" className="muted">
              {/* base is null for an IncomingPush branch or a legacy row (spec: no RootVersionId to
                  read it from) — the review still runs, just without the base-relative panels. */}
              The fork point for these versions is unknown, so this review shows the two versions
              themselves rather than what each changed since a common start.
            </p>
          )}

          {/* null (could not be computed) and [] (computed, no overlap) both render nothing here —
              neither is a warning, and conflating them would turn "nothing to report" into "look, an
              empty problem". */}
          {preview.overlaps && preview.overlaps.length > 0 && (
            <div data-testid="merge-overlaps" className="merge-overlaps">
              <h3>
                {preview.overlaps.length} paragraph{preview.overlaps.length === 1 ? '' : 's'} both sides
                touched
              </h3>
              <ul>
                {preview.overlaps.map((o) => (
                  <li key={o.ordinal}>{o.text}</li>
                ))}
              </ul>
              <p className="muted">
                A hint, not a guarantee — body-text paragraphs only. Review the changes below.
              </p>
            </div>
          )}

          <div className="merge-sides">
            {sidePanel('merge-side-main', 'Main', preview.main, redlines.main)}
            {sidePanel('merge-side-incoming', 'Incoming', preview.incoming, redlines.incoming)}
          </div>

          {!preview.available && (
            <p data-testid="merge-unavailable" role="alert" className="error">
              Comparison failed — download both versions and merge manually.
            </p>
          )}

          <p>
            Merging lands {preview.incoming.authorName}’s changes onto main as Word tracked changes.
            Nothing on either side is discarded, and the merge can be reverted afterward like any other
            version.
          </p>

          <div className="merge-actions">
            <button type="button" onClick={() => navigate(`/documents/${id}`)}>
              Cancel
            </button>
            <button type="button" disabled={!preview.available || merging} onClick={doMerge}>
              {merging ? 'Merging…' : 'Merge'}
            </button>
          </div>
        </>
      )}
    </section>
  )
}

// One shape for both sides of the panel — main and incoming carry the same fields and degrade the
// same way, so this is one function rather than two near-identical blocks of JSX.
function sidePanel(testId: string, label: string, side: MergeSideRow, html: string | null) {
  const available = html !== null && html.trim() !== UNAVAILABLE
  return (
    <div data-testid={testId}>
      <h3>
        {label} · <code>{side.number}</code>
      </h3>
      <p className="muted">{side.authorName}</p>
      {summaryLine(side.summary)}
      {html !== null && !available && (
        <p className="muted">A redline since the fork point is unavailable for this version.</p>
      )}
      {available && (
        // SANDBOXED ON PURPOSE — same reasoning as Compare.tsx's redline frame: this markup is
        // WmlComparer output from a user-uploaded .docx, so it is untrusted content. Rendering it via
        // dangerouslySetInnerHTML would put attacker-controlled markup straight into this origin's DOM,
        // where the session cookie lives. sandbox="" keeps every restriction on — no allow-scripts, no
        // allow-same-origin (that pair could let the frame shed its own sandbox) — and srcDoc means it
        // never touches the network. The redline has no scripts, forms, links or plugins to need any of
        // the allow-* tokens.
        <iframe
          className="editor-frame"
          title={`${label} redline since the fork point`}
          sandbox=""
          srcDoc={REDLINE_STYLE + html}
        />
      )}
    </div>
  )
}

function summaryLine(summary: ChangeSummary | null) {
  if (!summary) {
    // null, not "0 insertions, 0 deletions" — that leg simply could not be compared (spec: base is
    // unknown, or the base-relative diff itself failed), and 0/0 would misreport it as "no changes".
    return <p className="muted">This leg could not be compared.</p>
  }
  return (
    <p data-testid="merge-side-summary">
      {summary.insertions} insertions, {summary.deletions} deletions since the fork point
    </p>
  )
}

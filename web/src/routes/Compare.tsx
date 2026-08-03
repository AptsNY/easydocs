import { useEffect, useId, useState } from 'react'
import { Link, useParams } from 'react-router'
import {
  api,
  ApiError,
  getRaw,
  problemText,
  type ChangeSummary,
  type Paged,
  type VersionRow as Version,
} from '../api'

// The comparison / redline view (spec §7, §9) — a redline between any two versions of a document, even
// though nobody ever turned Track Changes on. This is the product's headline feature.
//
// The API answers the same pair in three formats; this screen reads two of them (summary for the counts,
// html for the rendering) and offers the third (docx) as a download.

// The API returns 200 text/html with EXACTLY this body when WmlComparer cannot produce a comparison —
// graceful degradation is deliberate (spec §12.2), so the failure has no status code to detect.
//
// ponytail: matching the sentinel string is the whole detection. Ceiling: a redline whose real content
// happened to be this one paragraph would be misread as unavailable — harmless, because a document that
// is one "Comparison unavailable." paragraph has no redline worth showing either. Upgrade path if it ever
// matters: an `X-EasyDocs-Diff: unavailable` response header on the html branch.
const UNAVAILABLE = '<p>Comparison unavailable.</p>'

export default function Compare() {
  const { id } = useParams()
  const fieldId = useId()
  const [versions, setVersions] = useState<Version[]>([])
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [counts, setCounts] = useState<ChangeSummary | null>(null)
  const [html, setHtml] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  // Newest first, like the console: the pair a reader wants is usually "the last two".
  //
  // ponytail: page one only (25 versions). Ceiling: an older version is not in the pickers. Upgrade path
  // when a document that long needs comparing: paginate the pickers, or accept ?from=&to= in the URL and
  // let history rows link a specific pair.
  useEffect(() => {
    if (!id) return
    api.get<Paged<Version>>(`/api/v1/documents/${id}/versions?order=desc`).then(
      (page) => {
        setVersions(page.items)
        setTo(page.items[0]?.id ?? '')
        setFrom((page.items[1] ?? page.items[0])?.id ?? '')
      },
      (e: unknown) => setError(problemText(e, 'Could not load this document’s versions.')),
    )
  }, [id])

  // Counts and rendering are one screen state, so they are fetched together — "2 insertions" above a
  // stale redline (or the reverse) would be worse than showing neither.
  useEffect(() => {
    if (!id || !from || !to) return
    let live = true
    const pair = `/api/v1/documents/${id}/compare?from=${from}&to=${to}`
    setBusy(true)
    Promise.all([
      // 422 = this pair cannot be compared, the same answer ?format=docx gives. Not an error to shout
      // about: the html leg below carries the explanation this screen shows, so the counts are simply
      // absent rather than a fabricated 0/0.
      api
        .get<ChangeSummary>(pair)
        .catch((e: unknown) =>
          e instanceof ApiError && e.status === 422 ? null : Promise.reject(e),
        ),
      getRaw(`${pair}&format=html`).then((r) => r.text()),
    ])
      .then(
        ([summary, rendered]) => {
          if (!live) return
          setError('')
          setCounts(summary)
          setHtml(rendered)
        },
        (e: unknown) => {
          if (!live) return
          setError(problemText(e, 'Could not compare these versions.'))
          setCounts(null)
          setHtml(null)
        },
      )
      .finally(() => {
        if (live) setBusy(false)
      })
    return () => {
      live = false
    }
  }, [id, from, to])

  const number = (vid: string) => versions.find((v) => v.id === vid)?.number ?? vid

  // A fetch-and-objectURL rather than navigating to ?format=docx: that response carries no
  // Content-Disposition (a navigation would save it as the route's name, "compare"), and its 422
  // "Comparison unavailable" would replace the screen with a problem+json document instead of an alert.
  const downloadRedline = async () => {
    try {
      setError('')
      const res = await getRaw(`/api/v1/documents/${id}/compare?from=${from}&to=${to}&format=docx`)
      const url = URL.createObjectURL(await res.blob())
      const a = document.createElement('a')
      a.href = url
      a.download = `redline-${number(from)}-to-${number(to)}.docx`
      // Attached before the click: a detached anchor does not download in every engine.
      document.body.append(a)
      a.click()
      a.remove()
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(problemText(e, 'Could not produce a redline document.'))
    }
  }

  const available = html !== null && html.trim() !== UNAVAILABLE
  // Only meaningful when a comparison was actually produced: an unavailable comparison also reports 0/0,
  // and calling that "no changes" would be a lie.
  const unchanged = available && counts?.insertions === 0 && counts.deletions === 0

  const picker = (label: string, value: string, onChange: (v: string) => void) => {
    const inputId = `${fieldId}-${label.replaceAll(' ', '-').toLowerCase()}`
    return (
      <span className="compare-picker">
        <label htmlFor={inputId}>{label}</label>
        <select id={inputId} value={value} onChange={(e) => onChange(e.target.value)}>
          {versions.map((v) => (
            <option key={v.id} value={v.id}>
              {v.number}
              {v.publishName ? ` · ${v.publishName}` : v.name ? ` · ${v.name}` : ''}
            </option>
          ))}
        </select>
      </span>
    )
  }

  return (
    <section data-testid="compare" className="compare">
      <h2>Compare versions</h2>
      <p>
        <Link to={`/documents/${id}`}>Back to the document</Link>
      </p>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      <div className="compare-pickers">
        {picker('From version', from, setFrom)}
        {picker('To version', to, setTo)}
      </div>

      {versions.length === 0 && !error && <p>This document has no versions to compare yet.</p>}

      {/* Insertions and deletions only. WmlComparer.GetRevisions classifies nothing else, so `moves` and
          `formatChanges` are permanently 0 — rendering them as counters would present a documented
          limitation as live data. */}
      {available && counts && (
        <p data-testid="compare-summary">
          {counts.insertions} insertions, {counts.deletions} deletions
        </p>
      )}

      {busy && html === null && <p>Comparing…</p>}

      {html !== null && !available && (
        <p data-testid="compare-unavailable" className="muted">
          A redline is unavailable for this pair — one of these versions could not be compared. Both are
          still downloadable from the history.
        </p>
      )}

      {unchanged && (
        <p data-testid="compare-empty" className="muted">
          {/* "No changes" would overclaim: the engine diffs body text only, and two versions can
              differ purely in formatting (highlights) or in headers/footers — real leases do. */}
          No changes to the body text between these two versions. Formatting and header/footer
          changes are not part of this comparison.
        </p>
      )}

      {available && !unchanged && (
        <>
          <button type="button" onClick={() => void downloadRedline()}>
            Download redline
          </button>

          {/* SANDBOXED ON PURPOSE — do not "simplify" this into dangerouslySetInnerHTML.
              This markup is generated by WmlComparer from a .docx a user uploaded, so it is untrusted
              content. Inlining it would put attacker-controlled markup in the app's own DOM, on the
              origin that holds the session — an XSS path straight through the headline feature.

              sandbox="" is the narrowest possible value: every restriction stays on. The redline is
              static HTML with no scripts, forms, links or plugins, so it needs none of the allow-*
              tokens — in particular not allow-scripts, and not allow-same-origin (which together would
              let the frame drop its own sandbox). srcDoc keeps it off the network entirely. */}
          <iframe
            data-testid="redline-frame"
            className="editor-frame"
            title="Redline comparison"
            sandbox=""
            srcDoc={html}
          />
        </>
      )}
    </section>
  )
}

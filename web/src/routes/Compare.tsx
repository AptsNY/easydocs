import { useEffect, useId, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import RedlinePages from '../components/RedlinePages'
import {
  api,
  ApiError,
  getRaw,
  problemText,
  type ChangeSummary,
  type Paged,
  type VersionRow as Version,
  changeCounts,
} from '../api'

// The comparison / redline view (spec §7, §9) — a redline between any two versions of a document, even
// though nobody ever turned Track Changes on. This is the product's headline feature.
//
// Counts come from ?format=summary; the redline itself is drawn as real pages (RedlinePages, format=pdf),
// and the .docx redline is offered as a download.

export default function Compare() {
  const { id } = useParams()
  const fieldId = useId()
  const [versions, setVersions] = useState<Version[]>([])
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [params] = useSearchParams()
  const [counts, setCounts] = useState<ChangeSummary | null>(null)
  // null = not known yet; false = this pair cannot be compared (summary 422).
  const [comparable, setComparable] = useState<boolean | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  // Newest first, like the console: the pair a reader wants is usually "the last two".
  //
  // ?from=&to= picks the pair when both are on the loaded page; otherwise the last two.
  //
  // ponytail: page one only (25 versions). Ceiling: an older version is not in the pickers. Upgrade path
  // when a document that long needs comparing: paginate the pickers.
  useEffect(() => {
    if (!id) return
    api.get<Paged<Version>>(`/api/v1/documents/${id}/versions?order=desc`).then(
      (page) => {
        setVersions(page.items)
        const has = (v: string | null) => (v && page.items.some((x) => x.id === v) ? v : null)
        setTo(has(params.get('to')) ?? page.items[0]?.id ?? '')
        setFrom(has(params.get('from')) ?? (page.items[1] ?? page.items[0])?.id ?? '')
      },
      (e: unknown) => setError(problemText(e, 'Could not load this document’s versions.')),
    )
    // Read once on arrival: after that the pickers own the pair.
  }, [id])

  // 422 = this pair cannot be compared (the same answer ?format=docx and pdf give), shown as such rather
  // than as a fabricated 0/0.
  useEffect(() => {
    if (!id || !from || !to) return
    let live = true
    setBusy(true)
    setComparable(null)
    api
      .get<ChangeSummary>(`/api/v1/documents/${id}/compare?from=${from}&to=${to}`)
      .then(
        (summary) => {
          if (!live) return
          setError('')
          setCounts(summary)
          setComparable(true)
        },
        (e: unknown) => {
          if (!live) return
          setCounts(null)
          if (e instanceof ApiError && e.status === 422) setComparable(false)
          else setError(problemText(e, 'Could not compare these versions.'))
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

  const available = comparable === true
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
          {changeCounts(counts)}
        </p>
      )}

      {busy && comparable === null && <p>Comparing…</p>}

      {comparable === false && (
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

          <RedlinePages
            documentId={id!}
            from={from}
            to={to}
            title={`Changes from ${number(from)} to ${number(to)}`}
          />
        </>
      )}
    </section>
  )
}

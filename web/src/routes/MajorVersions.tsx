import { useCallback, useEffect, useState } from 'react'
import { useOutletContext, useParams } from 'react-router'
import { api, problemText, type Paged, type Publication, type VersionRow as Version } from '../api'

// The Major Versions tab (spec §9, E6): the published versions of this document, newest first. A
// publication is a version that was renumbered from the document counter, so this is the "official"
// history sitting under the draft one.
export default function MajorVersions() {
  const { id } = useParams()
  const { tick } = useOutletContext<{ tick: number }>()
  const [rows, setRows] = useState<Publication[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [withPdf, setWithPdf] = useState<Set<string>>(new Set())
  const [error, setError] = useState('')

  const load = useCallback(
    async (cursor: string | null) => {
      const params = new URLSearchParams()
      if (cursor) params.set('cursor', cursor)
      const page = await api.get<Paged<Publication>>(`/api/v1/documents/${id}/publications?${params}`)
      setRows((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
    },
    [id],
  )

  useEffect(() => {
    load(null).catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  // Whether a PDF exists lives on the VERSION, not on the publication projection, so the flag is joined
  // from the version list. A PDF appears asynchronously (the render worker fans out `pdf.ready`), so this
  // re-reads on every tick like the rest of the console.
  //
  // ponytail: one extra read of the newest 100 versions rather than a GET per row. Ceiling: a publication
  // older than that window shows no PDF link even if it has one — the honest failure direction, since
  // offering a link to a PDF that does not exist would 409. Upgrade path: add hasPdf to the publications
  // projection and delete this effect.
  useEffect(() => {
    if (!id) return
    api
      .get<Paged<Version>>(`/api/v1/documents/${id}/versions?order=desc&limit=100`)
      .then((page) => setWithPdf(new Set(page.items.filter((v) => v.hasPdf).map((v) => v.id))))
      .catch(() => setWithPdf(new Set()))
  }, [id, tick])

  return (
    <div data-testid="major-versions">
      <h3>Major Versions</h3>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      <ol className="rows">
        {rows.map((p) => {
          const number = `${p.major}.${p.minor}.${p.revision}`
          return (
            <li key={p.versionId} data-testid="publication-row" data-kind={p.kind} data-number={number}>
              <span className="version-number">{number}</span>
              <span className="badge">{p.kind}</span>
              {p.name && <span data-testid="publication-name">{p.name}</span>}
              {/* A name, never the raw publishedBy id — resolved server-side by the AuthorNames helper. */}
              <span data-testid="publication-publisher">{p.publishedByName ?? '(unknown)'}</span>
              <time dateTime={p.publishedAt}>{new Date(p.publishedAt).toLocaleString()}</time>

              {/* Plain links: the download route sends Content-Disposition, so the browser saves the file
                  and never navigates. One per row, so each has to say which row it belongs to. */}
              <a data-testid="publication-docx" href={`/api/v1/versions/${p.versionId}/download`}>
                DOCX
                <span className="visually-hidden"> of version {number}</span>
              </a>
              {/* Only when a PDF actually exists: ?format=pdf answers 409 until the render worker has
                  produced one (and it never can where LibreOffice is absent), so an always-present link
                  would be an always-failing link. */}
              {withPdf.has(p.versionId) && (
                <a
                  data-testid="publication-pdf"
                  href={`/api/v1/versions/${p.versionId}/download?format=pdf`}
                >
                  PDF
                  <span className="visually-hidden"> of version {number}</span>
                </a>
              )}
            </li>
          )
        })}
      </ol>

      {rows.length === 0 && !error && (
        <p>Nothing published yet. Publish a version from its Actions menu in the history.</p>
      )}

      {nextCursor && (
        <button
          type="button"
          onClick={() => {
            load(nextCursor).catch((e: unknown) => setError(problemText(e)))
          }}
        >
          Load more
        </button>
      )}
    </div>
  )
}

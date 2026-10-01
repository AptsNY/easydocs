import { useEffect, useState } from 'react'

// A redline as real pages: compare?format=pdf (the WmlComparer redline through Gotenberg) in the browser's
// own PDF viewer — thumbnails, zoom, find and print come free. from === to shows one version on its own.
// Used by the History tab's version page and by the Compare screen.
export default function RedlinePages({
  documentId,
  from,
  to,
  title,
}: {
  documentId: string
  from: string
  to: string
  title: string
}) {
  const src = `/api/v1/documents/${documentId}/compare?from=${from}&to=${to}&format=pdf`
  // One probe before framing it: a failure (422, Gotenberg down, a pair that cannot be compared) would
  // otherwise show raw problem+json inside the viewer. A 200 is cached (immutable), so the frame's own load
  // costs nothing more.
  const [ok, setOk] = useState<boolean | null>(null)
  useEffect(() => {
    // Aborted when the pair changes: a superseded render stops in Gotenberg too (the server honours the
    // request abort) and does not count against the per-user render limit for nothing.
    const abort = new AbortController()
    setOk(null)
    fetch(src, { credentials: 'same-origin', signal: abort.signal }).then(
      (r) => setOk(r.ok),
      () => !abort.signal.aborted && setOk(false),
    )
    return () => abort.abort()
  }, [src])

  if (ok === null) return <p className="muted">Rendering the pages…</p>
  if (!ok)
    return (
      <p data-testid="version-pdf-unavailable">
        These versions could not be shown as pages.{' '}
        {from !== to && <a href={src.replace('format=pdf', 'format=docx')}>Download the redline (.docx)</a>}
      </p>
    )
  return (
    <>
      {/* key: a new pair is a new document, not a navigation inside the old viewer.
          #navpanes=0&view=FitH: the thumbnail strip starts closed and the page fits the frame's width, or
          the one thing this frame is for comes up at a third of its size. */}
      <iframe
        key={src}
        className="version-pdf"
        data-testid="version-pdf"
        title={title}
        src={`${src}#navpanes=0&view=FitH`}
      />
      <p className="muted">
        <a href={src} target="_blank" rel="noreferrer">
          Open the pages in a new tab
        </a>
      </p>
    </>
  )
}

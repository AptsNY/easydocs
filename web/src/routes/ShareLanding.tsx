import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { api } from '../api'

// The public share landing (spec §9, §11). Public on purpose: /s/{token} is the anonymous share view, so
// it sits outside RequireAuth and renders with no session at all.
//
// The recipient is the least technical person who will ever touch this product — a client, a lawyer, a
// counterparty who got a link in an email. They have no account here, so this screen carries NO app
// chrome: no organization name, no navigation, no sign-in prompt. Every one of those would be a dead end.
//
// GET /s/{token} content-negotiates. A browser navigation (Accept: text/html) gets the SPA shell before
// any database work, so it neither audits nor counts a view; this fetch — api.get sends
// Accept: application/json — is the hit that increments ViewCount and writes the anonymous
// share_link.viewed audit row. So it must happen exactly once per mount, and it does.
type Shared = { documentName: string; version: string; downloadUrl: string }

export default function ShareLanding() {
  const { token } = useParams()
  const [shared, setShared] = useState<Shared | null>(null)
  const [unavailable, setUnavailable] = useState(false)

  useEffect(() => {
    let live = true
    api.get<Shared>(`/s/${token}`).then(
      (s) => live && setShared(s),
      // ponytail: every failure lands on the same message, and it deliberately says nothing about WHY.
      // Revoked, expired and never-existed are one 404 in the API (ResolveLiveAsync) precisely so a
      // token cannot be probed; wording them apart here would hand that oracle back. A 500 or a dead
      // network reads the same, which costs a recipient nothing they could have acted on anyway.
      () => live && setUnavailable(true),
    )
    return () => {
      live = false
    }
  }, [token])

  if (unavailable)
    return (
      <main className="share" data-testid="share-landing">
        <p className="share-brand"><img className="brand-mark" src="/favicon.svg" alt="" />easydocs</p>
        <div className="share-card">
          <p role="alert" className="error" data-testid="share-unavailable">
            This link is no longer available. Shared links can expire or be turned off by the person who
            sent it — ask them for a new one.
          </p>
        </div>
      </main>
    )

  return (
    <main className="share" data-testid="share-landing">
      {/* Plain text, not a link: naming the product is reassurance, and there is nowhere here for this
          person to go. */}
      <p className="share-brand"><img className="brand-mark" src="/favicon.svg" alt="" />easydocs</p>

      <div className="share-card">
        {shared === null ? (
          <p className="muted">Opening the shared document…</p>
        ) : (
          <>
            <h1 data-testid="share-document-name">{shared.documentName}</h1>
            <p className="share-meta">
              Version <span data-testid="share-version">{shared.version}</span>
            </p>
            <p>Someone has shared a read-only copy of this Word document with you.</p>

            {/* A plain anchor: the response carries Content-Disposition: attachment, so the browser saves
                it and never navigates. There is no PDF on the public route — LibreOffice renders those,
                and it is not part of what a share link serves. */}
            <a className="button" href={shared.downloadUrl} download data-testid="share-download">
              Download the document
            </a>

            <p className="muted">
              Downloading opens in Microsoft Word, or anything else that reads .docx. You do not need an
              account.
            </p>
          </>
        )}
      </div>
    </main>
  )
}

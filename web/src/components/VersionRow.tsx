import type { ChangeSummary, DocRole, VersionRow as Version } from '../api'
import ActionsMenu from './ActionsMenu'

// One history row (spec §9), with the per-version Actions menu (E8) at the end of it.
//
// `role` is the caller's own role on this document, resolved once by the console. Until it arrives the menu
// is not rendered at all — a Viewer must never see the mutating actions flash before the filter catches up.
export default function VersionRow({
  version,
  documentId,
  role,
  onDone,
}: {
  version: Version
  documentId: string
  role: DocRole | null
  onDone: () => void
}) {
  return (
    <article
      className="version-row"
      data-testid="version-row"
      data-number={version.number}
      data-branch-kind={version.branchKind}
      data-source={version.source}
    >
      <span className="version-number" data-testid="version-number">
        {version.number}
      </span>
      {version.name && <span data-testid="version-name">{version.name}</span>}
      {version.publishedKind && (
        <span className="badge" data-testid="version-badge">
          {version.publishedKind}
          {version.publishName ? ` · ${version.publishName}` : ''}
        </span>
      )}
      <span data-testid="version-author">{version.createdByName}</span>
      {/* The API sends UTC; <time> keeps the machine-readable instant while the reader sees their clock. */}
      <time dateTime={version.createdAt}>{new Date(version.createdAt).toLocaleString()}</time>
      <span data-testid="version-summary">{summaryText(version.summary)}</span>

      {role && (
        <ActionsMenu version={version} documentId={documentId} role={role} onDone={onDone} />
      )}
    </article>
  )
}

// A dash, never "0 insertions": null means the diff has not been computed yet (or the version has no
// parent), and claiming zero changes would be a lie in both cases. `moves` and `formatChanges` are
// omitted rather than shown as 0 — WmlComparer never populates them, so displaying them would read as
// live data that is really a documented limitation.
function summaryText(summary: ChangeSummary | null) {
  if (!summary) return '—'
  return `${summary.insertions} insertions, ${summary.deletions} deletions`
}

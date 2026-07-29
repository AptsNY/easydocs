import type { ChangeSummary, VersionRow as Version } from '../api'

// One history row (spec §9). Task 13 drops the per-version Actions menu into the seam at the bottom.
export default function VersionRow({ version }: { version: Version }) {
  return (
    <article
      className="version-row"
      data-testid="version-row"
      data-number={version.number}
      data-branch-kind={version.branchKind}
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

      {/* Task 13: per-version Actions menu goes here. */}
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

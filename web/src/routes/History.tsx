import { useCallback, useEffect, useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router'
import { api, problemText, type DocRole, type Paged, type VersionRow as Version } from '../api'
import Row from '../components/VersionRow'
import RevisionGraph from '../components/RevisionGraph'

// Two renderings of the same page of versions: the indented list (spec §9, the default the
// conformance suite asserts) and the graphical DAG (issue #13), behind a toggle. Merging stays a
// list-view action; the graph is for reading the shape of the history.

type Group = {
  branchId: string
  kind: Version['branchKind']
  ordinal: number
  mergedInto: string | null
  rows: Version[] // newest first, like the request
}

export default function History() {
  const { id } = useParams()
  const { tick, myRole } = useOutletContext<{ tick: number; myRole: DocRole | null }>()
  const [rows, setRows] = useState<Version[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [view, setView] = useState<'list' | 'graph'>('list')

  // order=desc is opt-in on the API: ascending is the default because the C# conformance suite asserts
  // oldest-first. Reversing a page here instead would only ever reorder page one.
  const load = useCallback(
    async (cursor: string | null) => {
      const params = new URLSearchParams({ order: 'desc' })
      if (cursor) params.set('cursor', cursor)
      const page = await api.get<Paged<Version>>(`/api/v1/documents/${id}/versions?${params}`)
      setRows((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
    },
    [id],
  )

  useEffect(() => {
    load(null).catch((e: unknown) => setError(problemText(e)))
  }, [load, tick])

  // The Actions menu mutates versions, so it needs a way to say "re-read the list". SSE also ticks the
  // console for most of these, but an explicit refresh keeps a menu action correct even for the events the
  // stream does not carry (a fork publishes into the COPY's document, not this one).
  const refresh = useCallback(() => {
    load(null).catch((e: unknown) => setError(problemText(e)))
  }, [load])

  const rowProps: RowProps = { documentId: id!, role: myRole, onDone: refresh }

  const { spine, attached, detached } = layout(rows)
  // Merging needs main's head as the left side. Rows arrive newest-first, so that is the first of them.
  const mainHead = spine[0]?.id

  return (
    <div data-testid="history">
      <h3>History</h3>

      <div className="view-toggle" role="group" aria-label="History view">
        <button
          type="button"
          aria-pressed={view === 'list'}
          onClick={() => setView('list')}
        >
          List
        </button>
        <button
          type="button"
          data-testid="graph-toggle"
          aria-pressed={view === 'graph'}
          onClick={() => setView('graph')}
        >
          Graph
        </button>
      </div>

      {/* The comparison view's only entry point: it is a route of its own (spec §9 lists it as a screen,
          not a console tab), so without this link nothing in the app reaches it. */}
      <p>
        <Link to={`/documents/${id}/compare`}>Compare versions</Link>
      </p>

      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}

      {view === 'graph' && <RevisionGraph rows={rows} rowProps={rowProps} />}

      {view === 'list' && (
      <ol className="spine" data-testid="branch-spine">
        {spine.map((v) => (
          <li key={v.id}>
            <Row version={v} {...rowProps} />
            {/* A branch is indented under the version it forked from, which is where a reader looks
                for it. */}
            {attached.get(v.id)?.map((g) => (
              <BranchGroup key={g.branchId} group={g} mainHead={mainHead} rowProps={rowProps} />
            ))}
          </li>
        ))}
        {/* A group whose fork point is past the loaded page still has to render somewhere. */}
        {detached.map((g) => (
          <li key={g.branchId}>
            <BranchGroup group={g} mainHead={mainHead} rowProps={rowProps} />
          </li>
        ))}
      </ol>
      )}

      {rows.length === 0 && !error && <p>No versions yet.</p>}

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

// The three props every row needs beyond the version itself, bundled so BranchGroup forwards them rather
// than re-declaring them.
type RowProps = { documentId: string; role: DocRole | null; onDone: () => void }

function BranchGroup({
  group,
  mainHead,
  rowProps,
}: {
  group: Group
  mainHead: string | undefined
  rowProps: RowProps
}) {
  const concurrent = group.kind === 'Concurrent'
  // The review link needs the document id, and rowProps already carries the one this page was
  // routed for — threading a second copy of it down would only give the two a way to disagree.
  const { documentId, role } = rowProps
  // Merging is Editor+ (the preview and the POST both enforce it). The old button had no role test
  // either, but offering a Viewer a control that 403s got worse when it became navigation: instead of
  // an inline error beside the branch, they land on a screen headed "Review this merge" that can only
  // apologise. Spelled out rather than shared with the server's DocumentAuthorization.CanEdit, which
  // is not reachable from the client.
  const canMerge = role === 'Owner' || role === 'Editor'
  return (
    <section
      className="branch-group"
      data-testid="branch-group"
      data-kind={group.kind}
      data-merged={group.mergedInto ? 'true' : 'false'}
      aria-label={concurrent ? `Concurrent branch ${group.ordinal}` : 'Pushed from a copy'}
    >
      <h4>{concurrent ? `Concurrent branch ${group.ordinal}` : 'Pushed from a copy'}</h4>

      {group.mergedInto ? (
        <p className="muted">Merged into the main history.</p>
      ) : concurrent ? (
        // Merging is a decision, so it goes through the review screen rather than committing on click
        // (spec: 2026-08-24-three-way-merge-review-design.md). The POST itself is unchanged and still
        // lives on the API for callers that mean it.
        mainHead &&
        canMerge && (
          <Link
            className="button"
            to={`/documents/${documentId}/merge?left=${mainHead}&right=${group.rows[0].id}`}
          >
            Review &amp; merge
          </Link>
        )
      ) : (
        // An incoming push is reviewed (accept/reject) on the Copies tab, not merged by version id.
        <p className="muted">Review this push on the Copies tab.</p>
      )}

      <ol>
        {group.rows.map((v) => (
          <li key={v.id}>
            <Row version={v} {...rowProps} />
          </li>
        ))}
      </ol>
    </section>
  )
}

// Split a newest-first page into the main spine and its side branches, then hang each side branch off
// the version it forked from. The fork point is the parent of the branch's OLDEST row, which is its last
// one here — branch roots are not on the v1 API surface, but that parent is.
function layout(rows: Version[]) {
  const spine: Version[] = []
  const groups = new Map<string, Group>()

  for (const v of rows) {
    if (v.branchKind === 'Main') {
      spine.push(v)
      continue
    }
    const existing = groups.get(v.branchId)
    if (existing) existing.rows.push(v)
    else
      groups.set(v.branchId, {
        branchId: v.branchId,
        kind: v.branchKind,
        ordinal: v.branchOrdinal,
        mergedInto: v.branchMergedIntoVersionId,
        rows: [v],
      })
  }

  const onSpine = new Set(spine.map((v) => v.id))
  const attached = new Map<string, Group[]>()
  const detached: Group[] = []
  for (const g of [...groups.values()].sort((a, b) => a.ordinal - b.ordinal)) {
    const fork = g.rows[g.rows.length - 1].parentVersionId
    if (fork && onSpine.has(fork)) attached.set(fork, [...(attached.get(fork) ?? []), g])
    else detached.push(g)
  }
  return { spine, attached, detached }
}

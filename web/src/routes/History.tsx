import { useCallback, useEffect, useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router'
import { api, problemText, type DocRole, type Paged, type VersionRow as Version } from '../api'
import Row from '../components/VersionRow'

// ponytail: history is an indented list — main spine plus grouped concurrent-branch entries, per
// spec §9. The graphical DAG renderer is v1.1; most documents are linear until a concurrent edit.

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

  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
    } catch (e) {
      setError(problemText(e))
    }
    await load(null).catch((e: unknown) => setError(problemText(e)))
  }

  const rowProps: RowProps = { documentId: id!, role: myRole, onDone: refresh }

  const { spine, attached, detached } = layout(rows)
  // Merging needs main's head as the left side. Rows arrive newest-first, so that is the first of them.
  const mainHead = spine[0]?.id
  const merge = (right: string) =>
    void act(() => api.post(`/api/v1/documents/${id}/merges`, { left: mainHead, right }))

  return (
    <div data-testid="history">
      <h3>History</h3>

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

      <ol className="spine" data-testid="branch-spine">
        {spine.map((v) => (
          <li key={v.id}>
            <Row version={v} {...rowProps} />
            {/* A branch is indented under the version it forked from, which is where a reader looks
                for it. */}
            {attached.get(v.id)?.map((g) => (
              <BranchGroup
                key={g.branchId}
                group={g}
                canMerge={!!mainHead}
                onMerge={merge}
                rowProps={rowProps}
              />
            ))}
          </li>
        ))}
        {/* A group whose fork point is past the loaded page still has to render somewhere. */}
        {detached.map((g) => (
          <li key={g.branchId}>
            <BranchGroup group={g} canMerge={!!mainHead} onMerge={merge} rowProps={rowProps} />
          </li>
        ))}
      </ol>

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
  canMerge,
  onMerge,
  rowProps,
}: {
  group: Group
  canMerge: boolean
  onMerge: (right: string) => void
  rowProps: RowProps
}) {
  const concurrent = group.kind === 'Concurrent'
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
        // Merge takes main's head and this branch's head and lands a tracked-changes version on main.
        // Nothing is discarded, so there is no confirmation step to add.
        canMerge && (
          <button type="button" onClick={() => onMerge(group.rows[0].id)}>
            Merge
          </button>
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

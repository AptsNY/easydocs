import type { DocRole, VersionRow as Version } from '../api'
import Row from './VersionRow'

// The graphical revision DAG (issue #13): one lane per branch, a dot per version, edges to parents
// and from merge commits back down to the branch they merged. Rendered as one small SVG cell per
// row (absolutely filling a fixed-width column), with percentage y-coordinates so variable row
// heights need no measuring. Diagonals, not curves — same reason git log --graph uses slashes.
//
// ponytail: lanes are compacted by branch ordinal and never reused after a branch ends; a document
// with dozens of historical branches gets a wide gutter. Upgrade path: free lanes once a branch's
// last row has passed.

const LANE_W = 14
// Lane colours are CSS custom properties (--lane-0…5 in index.css): main is Core Blue, no lane is
// red (red means error, nothing else), and the dark theme swaps in lighter values. var() does not
// resolve inside SVG presentation attributes, so lines/dots take them via style.
const COLORS = [0, 1, 2, 3, 4, 5].map((i) => `var(--lane-${i})`)
const colorOf = (lane: number) => COLORS[lane % COLORS.length]

type Edge = { top: number; bottom: number; topLane: number; bottomLane: number; travelLane: number }

export type GraphRowProps = { documentId: string; role: DocRole | null; onDone: () => void }

export default function RevisionGraph({ rows, rowProps }: { rows: Version[]; rowProps: GraphRowProps }) {
  const idx = new Map(rows.map((v, i) => [v.id, i]))

  // Lane per branch: main is lane 0, the rest compacted in ordinal order.
  const branches = [...new Map(rows.map((v) => [v.branchId, v])).values()]
  const lanes = new Map<string, number>()
  for (const b of branches.filter((v) => v.branchKind === 'Main')) lanes.set(b.branchId, 0)
  branches
    .filter((v) => v.branchKind !== 'Main')
    .sort((a, b) => a.branchOrdinal - b.branchOrdinal)
    .forEach((v, i) => lanes.set(v.branchId, i + 1))
  const laneOf = (v: Version) => lanes.get(v.branchId) ?? 0
  const laneCount = Math.max(1, lanes.size)
  const x = (lane: number) => lane * LANE_W + LANE_W / 2

  const edges: Edge[] = []
  const addEdge = (a: number, b: number) => {
    const [top, bottom] = a < b ? [a, b] : [b, a]
    const [topLane, bottomLane] = [laneOf(rows[top]), laneOf(rows[bottom])]
    edges.push({ top, bottom, topLane, bottomLane, travelLane: Math.max(topLane, bottomLane) })
  }
  rows.forEach((v, i) => {
    const p = v.parentVersionId ? idx.get(v.parentVersionId) : undefined
    if (p !== undefined) addEdge(i, p)
  })
  // A merged branch connects its head up to the merge version on main.
  for (const b of branches) {
    const m = b.branchMergedIntoVersionId ? idx.get(b.branchMergedIntoVersionId) : undefined
    const head = rows.findIndex((v) => v.branchId === b.branchId)
    if (m !== undefined && head !== -1) addEdge(m, head)
  }

  // Everything an edge draws inside row k, in percentage y-space (0 top, 50 dot, 100 bottom).
  const segments = (k: number) =>
    edges.flatMap((e) => {
      const tx = x(e.travelLane)
      const color = colorOf(e.travelLane)
      if (k > e.top && k < e.bottom)
        return [{ x1: tx, y1: '0%', x2: tx, y2: '100%', color }]
      if (k === e.top) return [{ x1: x(e.topLane), y1: '50%', x2: tx, y2: '100%', color }]
      if (k === e.bottom) return [{ x1: tx, y1: '0%', x2: x(e.bottomLane), y2: '50%', color }]
      return []
    })

  return (
    <ol className="revision-graph" data-testid="history-graph">
      {rows.map((v, k) => (
        <li key={v.id}>
          <div className="graph-cell" style={{ width: laneCount * LANE_W }} aria-hidden="true">
            <svg width="100%" height="100%" data-testid="graph-cell">
              {segments(k).map((s, i) => (
                <line key={i} x1={s.x1} y1={s.y1} x2={s.x2} y2={s.y2} style={{ stroke: s.color }} strokeWidth={2} />
              ))}
              <circle
                cx={x(laneOf(v))}
                cy="50%"
                r={4}
                style={{ fill: colorOf(laneOf(v)) }}
                data-testid="graph-dot"
              />
            </svg>
          </div>
          <div className="graph-row">
            <Row version={v} {...rowProps} />
          </div>
        </li>
      ))}
    </ol>
  )
}

// One fetch wrapper for the whole SPA. `credentials: 'same-origin'` carries the httpOnly ed_session
// cookie; RFC-7807 problem+json bodies (spec §10) become a typed ApiError so screens can show `detail`
// instead of "something went wrong".
// Fields assigned in the body rather than as constructor parameter properties: tsconfig sets
// `erasableSyntaxOnly`, which bans the shorthand.
export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly detail: string

  constructor(status: number, title: string, detail: string) {
    super(detail || title)
    this.status = status
    this.title = title
    this.detail = detail
  }
}

type Problem = { title?: string; detail?: string }

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...(body === undefined || body instanceof FormData
        ? {}
        : { 'Content-Type': 'application/json' }),
    },
    body: body instanceof FormData ? body : body === undefined ? undefined : JSON.stringify(body),
  })

  if (!res.ok) await fail(res)
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

async function fail(res: Response): Promise<never> {
  const problem: Problem | null = await res.json().catch(() => null)
  throw new ApiError(res.status, problem?.title ?? res.statusText, problem?.detail ?? '')
}

// Two v1 reads are not JSON: the redline HTML (text/html) and the redline .docx (a stream). They still
// need the same cookie and the same problem+json failure, so they share one raw read and pick their own
// body decoder, rather than growing a second wrapper per content type.
export async function getRaw(path: string): Promise<Response> {
  const res = await fetch(path, { credentials: 'same-origin' })
  if (!res.ok) await fail(res)
  return res
}

// Every screen needs the same "show the API's own words" line, so it lives next to ApiError rather than
// being re-typed per route. The fallback is per-caller because "Folder request failed." reads better
// under a folder tree than a generic apology.
export function problemText(e: unknown, fallback = 'Something went wrong.') {
  return e instanceof ApiError ? e.detail || e.title : fallback
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
  patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
  del: <T>(path: string) => request<T>('DELETE', path),
}

// ponytail: these types are maintained by hand against the C# projections (DocumentListProjection,
// VersionListProjection, OrgEndpoints). One consumer, so hand-maintenance is cheaper than a codegen
// step in the build. Upgrade path if a second consumer appears: generate a client from
// /openapi/v1.json and delete everything below.

export type Me = { id: string; email: string; displayName: string; orgId: string }
export type Org = { id: string; name: string; slug: string; myRole: string }
// A user belongs to one org per session but can be a member of several — their own, from registration,
// plus any they were invited into. `current` is which one this session is bound to.
export type OrgMembership = Org & { current: boolean }
export type Folder = { id: string; name: string; parentId: string | null }

export type Tile = {
  id: string
  name: string
  folderId: string | null
  currentNumber: string | null
  versionCount: number
  updatedAt: string | null
  lastAuthorName: string | null
  deletedAt: string | null
}

export type ChangeSummary = {
  insertions: number
  deletions: number
  moves: number
  formatChanges: number
}

export type VersionRow = {
  id: string
  major: number
  minor: number
  revision: number
  number: string
  name: string | null
  source: string
  publishedKind: string | null
  publishedAt: string | null
  publishName: string | null
  hasPdf: boolean
  parentVersionId: string | null
  branchId: string
  branchKind: 'Main' | 'Concurrent' | 'IncomingPush'
  branchOrdinal: number
  branchMergedIntoVersionId: string | null
  createdBy: string
  createdByName: string
  createdAt: string
  summary: ChangeSummary | null
}

export type Paged<T> = { items: T[]; nextCursor: string | null }

// POST /api/v1/documents:import -- the response names both the document AND the version its bytes
// landed in, because there is no follow-up read: the dashboard navigates straight to the console with
// nothing else to ask the API for.
export type ImportedDocument = {
  id: string
  name: string
  folderId: string | null
  versionId: string
  major: number
  minor: number
  revision: number
}

// GET /api/v1/documents/{id} — the console header. Deliberately thinner than Tile: no counts, because
// the console reads the version list anyway.
export type DocumentDetail = { id: string; name: string; folderId: string | null; orgId: string }

export type DocRole = 'Owner' | 'Editor' | 'Viewer'

// GET /api/v1/documents/{id}/members returns a BARE array, not a Paged<T> — the roster is small enough
// that it was never paginated.
export type Member = {
  userId: string
  email: string
  displayName: string
  role: DocRole
  createdAt: string
}

// GET /api/v1/documents/{id}/publications — the Major Versions tab. `publishedByName` is resolved
// server-side by the shared AuthorNames helper, like every other read surface.
export type Publication = {
  versionId: string
  major: number
  minor: number
  revision: number
  name: string | null
  publishedBy: string
  publishedByName: string | null
  publishedAt: string
  kind: 'minor' | 'major'
}

// GET /api/v1/documents/{id}/copies returns a BARE array. A copy is a separate document with its own
// members and its own history, so all this carries is enough to name it and link to it.
export type Copy = {
  id: string
  name: string
  parentDocumentId: string | null
  forkedFromVersionId: string | null
  versionId: string | null
  createdAt: string
}

// GET /api/v1/documents/{id}/push-requests — also a bare array, and it answers for BOTH directions: rows
// where targetDocumentId is this document are inbound (to review), rows where copyDocumentId is are
// outbound (to follow). One route, because a pusher may hold no role on the target at all.
export type PushRequest = {
  id: string
  status: 'pending' | 'accepted' | 'rejected' | 'auto_accepted'
  copyDocumentId: string
  targetDocumentId: string
  sourceVersionId: string
  materializedVersionId: string | null
  pushedBy: string
  createdAt: string
  decidedAt: string | null
}

// GET /api/v1/documents/{id}/audit — newest first. `actorUserId` and `actorName` are BOTH null for an
// anonymous public share-link read: that row has no actor, which is not the same as an unresolvable one.
export type AuditRow = {
  id: string
  action: string
  actorUserId: string | null
  actorName: string | null
  targetType: string | null
  targetId: string | null
  metadata: string | null
  createdAt: string
}

// POST .../members answers one of two shapes: a direct grant when the email is already in the org, or an
// invitation whose raw token is returned exactly once (only its hash is stored).
export type MemberAdded = {
  userId?: string
  email: string
  role: DocRole
  invitationToken?: string
}

// GET /api/v1/approvals (paged, the inbox) and GET /api/v1/versions/{vid}/approvals (a BARE array, one
// version's panel) answer the same row. `documentName` and `versionNumber` are denormalised into it on
// purpose, so an inbox renders with no follow-up request per item, and `status` is DERIVED server-side:
// "cancelled" if cancelled, else the decision, else "open" — cancel wins over a decision that can never
// arrive.
export type Approval = {
  id: string
  versionId: string
  documentId: string
  documentName: string
  versionNumber: string
  approverId: string
  approverName: string
  requestedBy: string
  requestedByName: string
  decision: string | null
  decisionComment: string | null
  dueAt: string | null
  decidedAt: string | null
  cancelledAt: string | null
  status: 'open' | 'approved' | 'rejected' | 'cancelled'
  createdAt: string
}

// GET /api/v1/tokens — a BARE array, and the name field is `serviceName`, not `name` (the create request
// calls it `name`). Revoking is a soft revoke: the row stays, with revokedAt set.
export type ApiTokenRow = {
  id: string
  serviceName: string
  scopes: string[]
  expiresAt: string | null
  lastUsedAt: string | null
  revokedAt: string | null
  createdAt: string
}

// GET /api/v1/documents/{id}/share-links — paged, newest first, and DOCUMENT-scoped even though a link
// points at one version: "what have I shared?" is a question about the document. Revoked and expired rows
// are included and flagged. No token and no hash: the raw token is unrecoverable by design (spec §11) and
// the hash is not the UI's business — `id` is all a revoke needs.
export type ShareLinkRow = {
  id: string
  versionId: string
  versionNumber: string
  createdBy: string
  createdByName: string
  createdAt: string
  expiresAt: string | null
  revokedAt: string | null
  viewCount: number
}

export type OrgRole = 'Owner' | 'Admin' | 'Member'

// GET /api/v1/org/members — a BARE array, readable by ANY org member because the SPA's person pickers need
// it. Org role grants nothing on a document (spec §11): this roster is not a document roster.
export type OrgMember = {
  userId: string
  email: string
  displayName: string
  role: OrgRole
  createdAt: string
}

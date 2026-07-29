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

  if (!res.ok) {
    const problem: Problem | null = await res.json().catch(() => null)
    throw new ApiError(res.status, problem?.title ?? res.statusText, problem?.detail ?? '')
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
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

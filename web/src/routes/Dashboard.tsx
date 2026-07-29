import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, problemText, type Paged, type Tile } from '../api'
import FolderTree, { useFolderTree } from '../components/FolderTree'

// Spec §9's dashboard: folder tree, document tiles, name search — plus the trash view, which is the
// half that did not exist before M4.5. DELETE soft-deleted and :restore restored, but nothing listed
// trashed documents, so recovering one meant having kept its GUID.
export default function Dashboard({ trashed = false }: { trashed?: boolean }) {
  const { folderId } = useParams()
  const tree = useFolderTree()

  const [tiles, setTiles] = useState<Tile[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [typed, setTyped] = useState('')
  const [q, setQ] = useState('')
  const [docName, setDocName] = useState('')
  const [error, setError] = useState('')

  // Debounced so a five-letter word is one query, not five. The filter runs server-side (?q=): the
  // list is cursor-paginated, so filtering the loaded page would hide matches that live past it.
  useEffect(() => {
    const t = setTimeout(() => setQ(typed), 250)
    return () => clearTimeout(t)
  }, [typed])

  const load = useCallback(
    async (cursor: string | null) => {
      const params = new URLSearchParams()
      if (folderId) params.set('folderId', folderId)
      if (q) params.set('q', q)
      if (trashed) params.set('trashed', 'true')
      if (cursor) params.set('cursor', cursor)
      const page = await api.get<Paged<Tile>>(`/api/v1/documents?${params}`)
      setTiles((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
    },
    [folderId, q, trashed],
  )

  useEffect(() => {
    load(null).catch((e: unknown) => setError(problemText(e)))
  }, [load])

  // Every mutation goes through here so a failure always says why. A silent no-op is the worst
  // outcome available.
  const act = async (fn: () => Promise<unknown>) => {
    try {
      setError('')
      await fn()
      await load(null)
    } catch (e) {
      setError(problemText(e))
    }
  }

  const folderName = tree.known.find((f) => f.id === folderId)?.name

  return (
    <section className="dashboard" data-testid="dashboard">
      {!trashed && <FolderTree tree={tree} currentId={folderId ?? null} />}

      <div className="docs">
        {/* ponytail: two API-shaped compromises in one line. At the root the heading says "All
            documents" because it IS all of them — GET /documents cannot ask for "folderId is null"
            (folderId is a Guid?, so omitting it means no filter at all). And the folder's name comes
            from whatever the tree has loaded, so a cold deep link to /folders/{id} reads "Folder"
            until you expand to it — there is no GET /folders/{id}. Upgrade path: a folderId=none
            sentinel in ListDocuments, and a single-folder GET. */}
        <h2>{trashed ? 'Trash' : (folderName ?? (folderId ? 'Folder' : 'All documents'))}</h2>

        {trashed ? (
          <p>Trashed documents keep their history. Restoring one puts it back where it was.</p>
        ) : (
          <>
            <Link to="/trash" data-testid="trash-link">
              Trash
            </Link>

            <form className="stack" role="search" onSubmit={(e) => e.preventDefault()}>
              <label htmlFor="search">Search documents</label>
              <input
                id="search"
                data-testid="search"
                type="search"
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
              />
            </form>

            <form
              className="stack"
              data-testid="new-document"
              onSubmit={(e) => {
                e.preventDefault()
                if (!docName.trim()) return
                void act(() =>
                  api
                    .post('/api/v1/documents', {
                      name: docName.trim(),
                      folderId: folderId ?? null,
                    })
                    .then(() => setDocName('')),
                )
              }}
            >
              <label htmlFor="doc-name">Document name</label>
              <input
                id="doc-name"
                value={docName}
                onChange={(e) => setDocName(e.target.value)}
              />
              <button type="submit">Create document</button>
            </form>
          </>
        )}

        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}

        <ul className="tiles">
          {tiles.map((t) => (
            <li key={t.id} data-testid="document-tile" data-name={t.name}>
              <Link to={`/documents/${t.id}`}>{t.name}</Link>

              <p data-testid="tile-version">
                {t.currentNumber
                  ? `${t.currentNumber} · ${t.versionCount} version${t.versionCount === 1 ? '' : 's'}`
                  : 'No versions yet'}
              </p>
              <p data-testid="tile-updated">{t.updatedAt ? whenLocal(t.updatedAt) : ''}</p>
              <p data-testid="tile-author">{t.lastAuthorName ?? ''}</p>

              {trashed ? (
                <button
                  type="button"
                  data-testid="restore-button"
                  onClick={() => void act(() => api.post(`/api/v1/documents/${t.id}:restore`))}
                >
                  Restore
                </button>
              ) : (
                <div className="tile-actions">
                  <label>
                    <span>Upload version</span>
                    <input
                      type="file"
                      data-testid="upload-input"
                      accept=".docx"
                      onChange={(e) => {
                        const input = e.currentTarget
                        const file = input.files?.[0]
                        if (!file) return
                        const body = new FormData()
                        body.append('file', file)
                        input.value = '' // so re-picking the same file fires change again
                        void act(() => api.post(`/api/v1/documents/${t.id}/versions`, body))
                      }}
                    />
                  </label>

                  {/* PATCH folderId is the move (E1: history and members come along). The API cannot
                      set folderId back to null, so there is no "move to top level" option. */}
                  <label>
                    <span>Move to</span>
                    <select
                      value=""
                      onChange={(e) => {
                        const target = e.target.value
                        if (!target) return
                        void act(() =>
                          api.patch(`/api/v1/documents/${t.id}`, { folderId: target }),
                        )
                      }}
                    >
                      <option value="">Choose a folder…</option>
                      {tree.known
                        .filter((f) => f.id !== t.folderId)
                        .map((f) => (
                          <option key={f.id} value={f.id}>
                            {f.name}
                          </option>
                        ))}
                    </select>
                  </label>

                  <button
                    type="button"
                    onClick={() => void act(() => api.del(`/api/v1/documents/${t.id}`))}
                  >
                    Move to trash
                  </button>
                </div>
              )}
            </li>
          ))}
        </ul>

        {tiles.length === 0 && <p>{trashed ? 'The trash is empty.' : 'No documents here yet.'}</p>}
        {/* Appends rather than going through act(), which reloads from the first page. */}
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
    </section>
  )
}

// The API sends UTC; the tile shows the reader's own clock.
function whenLocal(iso: string) {
  return new Date(iso).toLocaleString()
}

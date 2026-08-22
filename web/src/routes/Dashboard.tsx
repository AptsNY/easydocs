import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { api, problemText, type ImportedDocument, type Paged, type Tile } from '../api'
import FolderTree, { useFolderTree } from '../components/FolderTree'

// Spec §9's dashboard: folder tree, document tiles, name search — plus the trash view, which is the
// half that did not exist before M4.5. DELETE soft-deleted and :restore restored, but nothing listed
// trashed documents, so recovering one meant having kept its GUID.

// One select rather than a key picker plus a direction toggle: six labelled choices is one control
// and one piece of state, and every label says what you will get rather than naming a column.
const SORTS = [
  ['updated:desc', 'Last updated'],
  ['updated:asc', 'Oldest updated'],
  ['name:asc', 'Name A–Z'],
  ['name:desc', 'Name Z–A'],
  ['created:desc', 'Newest first'],
  ['created:asc', 'Oldest first'],
] as const

export default function Dashboard({ trashed = false }: { trashed?: boolean }) {
  const { folderId } = useParams()
  const navigate = useNavigate()
  const tree = useFolderTree()

  const [tiles, setTiles] = useState<Tile[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [typed, setTyped] = useState('')
  const [q, setQ] = useState('')
  const [docName, setDocName] = useState('')
  const [importFile, setImportFile] = useState<File | null>(null)
  const [importName, setImportName] = useState('')
  // The name the LAST picked file derived, kept separately from importName so a second pick can tell
  // "the field still shows what picking a file put there" apart from "the user typed this on
  // purpose." Comparing only against importName would make every pick look like a fresh, safe-to-
  // overwrite field, including the one right after someone types a name and then swaps the file.
  const [derivedImportName, setDerivedImportName] = useState('')
  const [error, setError] = useState('')

  // In the URL, not in state: a sorted view survives a reload, is shareable as a link, and comes
  // back when you return from a document. The API's own default is created-asc — the web client opts
  // into last-updated-first the same way History opts into order=desc.
  const [params, setParams] = useSearchParams()
  // Not trusted straight from the URL: a hand-typed or stale pair that is not one of the six options
  // would leave the select showing index 0 -- "Last updated" -- while the list obeyed something else,
  // and picking that option would fire no change event, so the obvious way back would be a dead control.
  const pair = `${params.get('sort') ?? 'updated'}:${params.get('order') ?? 'desc'}`
  const [sort, order] = (SORTS.some(([value]) => value === pair) ? pair : SORTS[0][0]).split(':')

  // Debounced so a five-letter word is one query, not five. The filter runs server-side (?q=): the
  // list is cursor-paginated, so filtering the loaded page would hide matches that live past it.
  useEffect(() => {
    const t = setTimeout(() => setQ(typed), 250)
    return () => clearTimeout(t)
  }, [typed])

  const load = useCallback(
    async (cursor: string | null) => {
      const search = new URLSearchParams()
      if (folderId) search.set('folderId', folderId)
      if (q) search.set('q', q)
      if (trashed) search.set('trashed', 'true')
      if (cursor) search.set('cursor', cursor)
      search.set('sort', sort)
      search.set('order', order)
      const page = await api.get<Paged<Tile>>(`/api/v1/documents?${search}`)
      setTiles((prev) => (cursor ? [...prev, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
    },
    [folderId, q, trashed, sort, order],
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
    <section className={trashed ? 'dashboard trash' : 'dashboard'} data-testid="dashboard">
      {!trashed && <FolderTree tree={tree} currentId={folderId ?? null} />}

      <div className="docs">
        {/* ponytail: two API-shaped compromises in one line. At the root the heading says "All
            documents" because it IS all of them — GET /documents cannot ask for "folderId is null"
            (folderId is a Guid?, so omitting it means no filter at all). And the folder's name comes
            from whatever the tree has loaded, so a cold deep link to /folders/{id} reads "Folder"
            until you expand to it — there is no GET /folders/{id}. Upgrade path: a folderId=none
            sentinel in ListDocuments, and a single-folder GET. */}
        <header className="docs-head">
          <h2>{trashed ? 'Trash' : (folderName ?? (folderId ? 'Folder' : 'All documents'))}</h2>

          {trashed ? (
            <p className="muted">
              Trashed documents keep their history. Restoring one puts it back where it was.
            </p>
          ) : (
            // Navigation and one primary action, not a form dump: the search field is always here
            // because it is how you get around a library, and everything that WRITES is folded away
            // behind its own verb. Native <details>, so the disclosure costs no JavaScript and its
            // summary is focusable and Enter-operable for free.
            <div className="docs-tools">
              <form className="search" role="search" onSubmit={(e) => e.preventDefault()}>
                <label className="visually-hidden" htmlFor="search">
                  Search documents
                </label>
                <input
                  id="search"
                  data-testid="search"
                  type="search"
                  placeholder="Search names & content"
                  value={typed}
                  onChange={(e) => setTyped(e.target.value)}
                />
              </form>

              <label className="inline-field sort">
                <span>Sort</span>
                <span className="visually-hidden"> documents</span>
                <select
                  data-testid="sort"
                  value={`${sort}:${order}`}
                  onChange={(e) => {
                    const [s, o] = e.target.value.split(':')
                    const next = new URLSearchParams(params)
                    next.set('sort', s)
                    next.set('order', o)
                    setParams(next)
                    // "Load more" renders on nextCursor, so leaving it set keeps the button live and
                    // clickable through the refetch — and the cursor it holds belongs to the sort the
                    // user just left, a pair the backend rejects with a message written for API clients.
                    setNextCursor(null)
                  }}
                >
                  {SORTS.map(([value, label]) => (
                    <option key={value} value={value}>
                      {label}
                    </option>
                  ))}
                </select>
              </label>

              <details className="disclose" data-testid="new-document">
                <summary>New document</summary>
                <form
                  className="stack"
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
                  {/* Hidden, not removed: one field under a summary that already says "New
                      document" needs no second shouted label, but it still has to be nameable. */}
                  <label className="visually-hidden" htmlFor="doc-name">
                    Document name
                  </label>
                  <input
                    id="doc-name"
                    placeholder="Document name"
                    value={docName}
                    onChange={(e) => setDocName(e.target.value)}
                  />
                  <button type="submit">Create document</button>
                </form>
              </details>

              <details className="disclose" data-testid="import-document">
                <summary>Import document</summary>
                <form
                  className="stack"
                  onSubmit={(e) => {
                    e.preventDefault()
                    if (!importFile) return
                    const body = new FormData()
                    body.append('file', importFile)
                    // Omitted rather than sent empty: an empty string is still a name to the API, and
                    // the endpoint's own filename-derived default (spec-required for a bare import) is
                    // better than this form re-deriving it a second time and risking a mismatch.
                    if (importName.trim()) body.append('name', importName.trim())
                    if (folderId) body.append('folderId', folderId)
                    void act(() =>
                      api.post<ImportedDocument>('/api/v1/documents:import', body).then((doc) => {
                        setImportFile(null)
                        setImportName('')
                        setDerivedImportName('')
                        navigate(`/documents/${doc.id}`)
                      }),
                    )
                  }}
                >
                  {/* Same clipped-input-under-its-own-label trick as "Upload version" below: the OS
                      file-choose control is unstyleable, so the label IS the button, and
                      .visually-hidden (not display:none) keeps the input focusable and settable. This
                      one appears once on the page rather than once per tile, so its name needs no
                      per-document suffix to disambiguate it. */}
                  <label className="filebutton">
                    <span>Choose file</span>
                    <input
                      type="file"
                      className="visually-hidden"
                      data-testid="import-input"
                      accept=".docx"
                      onChange={(e) => {
                        const input = e.currentTarget
                        const file = input.files?.[0]
                        input.value = '' // so re-picking the same file still fires change
                        if (!file) return
                        const stem = stemOf(file.name)
                        // The one rule that matters here: never clobber a name the user typed. A blank
                        // field or one still holding what the PREVIOUS pick derived is fair game to
                        // replace; anything else is a name someone chose on purpose, including after
                        // picking the wrong file first and swapping it for the right one. Comparing
                        // against derivedImportName (not just "is it empty") is what makes that
                        // distinction possible -- drop it and the two cases become indistinguishable,
                        // and the obvious fix is an unconditional overwrite that silently eats the name.
                        setImportName((prev) => (prev === '' || prev === derivedImportName ? stem : prev))
                        setDerivedImportName(stem)
                        setImportFile(file)
                      }}
                    />
                  </label>

                  <label className="visually-hidden" htmlFor="import-name">
                    Document name
                  </label>
                  <input
                    id="import-name"
                    placeholder="Document name"
                    value={importName}
                    onChange={(e) => {
                      setImportName(e.target.value)
                    }}
                  />
                  <button type="submit">Import</button>
                </form>
              </details>

              <Link to="/trash" className="quiet" data-testid="trash-link">
                Trash
              </Link>
            </div>
          )}
        </header>

        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}

        <ul className="tiles">
          {tiles.map((t) => (
            <li key={t.id} className="tile" data-testid="document-tile" data-name={t.name}>
              {/* THE WHOLE TILE IS THE TARGET, and it is still one real <a href>: .tile-open::after is
                  stretched over the card, so the hit area is the anchor's own box — middle-click and
                  open-in-new-tab keep working, and nothing interactive is nested inside an anchor
                  (which would be invalid HTML and unreadable to a screen reader). The controls below
                  sit on a higher layer so they receive their own clicks.

                  ponytail: the overlay also swallows text selection on the card's meta lines. The
                  known cost of this pattern; the upgrade path is a click handler that ignores events
                  whose target is inside a control, which is more code and one more way to be wrong. */}
              <h3 className="tile-name">
                <Link className="tile-open" to={`/documents/${t.id}`}>
                  {t.name}
                </Link>
              </h3>

              <p className="tile-version" data-testid="tile-version">
                {t.currentNumber
                  ? `${t.currentNumber} · ${t.versionCount} version${t.versionCount === 1 ? '' : 's'}`
                  : 'No versions yet'}
              </p>
              <p className="tile-when">
                {t.updatedAt ? <span data-testid="tile-updated">{whenLocal(t.updatedAt)}</span> : null}
                {t.lastAuthorName ? (
                  <span data-testid="tile-author">{t.lastAuthorName}</span>
                ) : null}
              </p>

              {trashed ? (
                // Every one of these controls exists once per tile, so each has to say WHICH document it
                // acts on — the same visually-hidden suffix the console tabs already use for their
                // repeated Accept/Reject/Cancel buttons.
                <button
                  type="button"
                  data-testid="restore-button"
                  onClick={() => void act(() => api.post(`/api/v1/documents/${t.id}:restore`))}
                >
                  Restore
                  <span className="visually-hidden"> {t.name}</span>
                </button>
              ) : (
                <details className="disclose tile-more" data-testid="tile-more">
                  <summary>
                    Manage
                    <span className="visually-hidden"> {t.name}</span>
                  </summary>
                  <div className="tile-actions">
                    {/* The OS "Choose File" button is the one control in the product nobody designed,
                        so the input is clipped to a screen-reader-only box and its own <label> is the
                        button. Still label-associated and still settable — .visually-hidden clips, it
                        does not display:none — and :focus-within paints the ring on the label, so a
                        keyboard reader sees where they are. */}
                    <label className="filebutton">
                      <span>Upload version</span>
                      <span className="visually-hidden"> of {t.name}</span>
                      <input
                        type="file"
                        className="visually-hidden"
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
                    <label className="inline-field">
                      <span>Move to</span>
                      <span className="visually-hidden"> folder, for {t.name}</span>
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
                      className="danger"
                      onClick={() => void act(() => api.del(`/api/v1/documents/${t.id}`))}
                    >
                      Move to trash
                      <span className="visually-hidden"> — {t.name}</span>
                    </button>
                  </div>
                </details>
              )}
            </li>
          ))}
        </ul>

        {/* An empty column is not an answer. "No results" and "nothing here yet" are different
            situations and get different words — one is a dead end, the other is a first step. */}
        {tiles.length === 0 &&
          (trashed ? (
            <p className="empty">The trash is empty.</p>
          ) : q ? (
            <p className="empty">
              Nothing matches “{q}”. Try a shorter word, or clear the search to see everything.
            </p>
          ) : (
            <p className="empty">
              No documents {folderId ? 'in this folder' : 'yet'}. Create one with{' '}
              <b>New document</b>, then upload a .docx to start its history.
            </p>
          ))}
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

// Mirrors DeriveNameFromFileName in DocumentEndpoints.cs for the PREFILL only -- the server still
// derives its own copy of record if name is omitted, so this only has to be close enough to show the
// user what they're about to get, not byte-identical. It also gets to skip the server's `\`-separator
// handling: a browser file input hands back a bare filename, never a path.
function stemOf(fileName: string) {
  const dot = fileName.lastIndexOf('.')
  return dot > 0 ? fileName.slice(0, dot) : fileName
}

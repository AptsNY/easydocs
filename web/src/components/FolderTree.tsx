import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, problemText, type Folder } from '../api'

const ROOT = 'root'
export type DeleteMode = 'trash' | 'promote_children'

// GET /api/v1/folders returns ONE level: no parentId for the root, ?parentId=<guid> for a folder's
// children. An empty ?parentId= is a 400 (Guid binding), so the param is omitted, never blanked.
// One request per expansion is exactly what that endpoint is shaped for.
export type FolderTreeState = {
  known: Folder[]
  childrenOf: (parentId: string | null) => Folder[] | undefined
  isExpanded: (id: string) => boolean
  toggle: (id: string) => void
  expand: (id: string) => void
  create: (name: string, parentId: string | null) => Promise<boolean>
  rename: (id: string, name: string, parentId: string | null) => Promise<boolean>
  remove: (id: string, mode: DeleteMode) => Promise<boolean>
  error: string
}

// The state lives in a hook rather than inside <FolderTree> because the dashboard's per-tile "Move to"
// select and its heading both need the folders the tree has loaded — one cache, two consumers.
export function useFolderTree(): FolderTreeState {
  const [children, setChildren] = useState<Record<string, Folder[]>>({})
  const [expanded, setExpanded] = useState<Record<string, boolean>>({})
  const [error, setError] = useState('')

  const load = useCallback(async (parentId: string | null) => {
    const rows = await api.get<Folder[]>(
      parentId ? `/api/v1/folders?parentId=${parentId}` : '/api/v1/folders',
    )
    setChildren((c) => ({ ...c, [parentId ?? ROOT]: rows }))
  }, [])

  const run = useCallback(async (fn: () => Promise<void>) => {
    try {
      setError('')
      await fn()
      return true
    } catch (e) {
      setError(problemText(e, 'Folder request failed.'))
      return false
    }
  }, [])

  useEffect(() => {
    void run(() => load(null))
  }, [run, load])

  const expand = useCallback(
    (id: string) => {
      setExpanded((e) => ({ ...e, [id]: true }))
      if (!(id in children)) void run(() => load(id))
    },
    [children, load, run],
  )

  return {
    known: Object.values(children).flat(),
    childrenOf: (parentId) => children[parentId ?? ROOT],
    isExpanded: (id) => !!expanded[id],
    toggle: (id) => (expanded[id] ? setExpanded((e) => ({ ...e, [id]: false })) : expand(id)),
    expand,
    error,
    create: (name, parentId) =>
      run(async () => {
        await api.post('/api/v1/folders', { name, parentId })
        await load(parentId)
        if (parentId) setExpanded((e) => ({ ...e, [parentId]: true }))
      }),
    rename: (id, name, parentId) =>
      run(async () => {
        await api.patch(`/api/v1/folders/${id}`, { name })
        await load(parentId)
      }),
    // ponytail: a delete drops the whole cache and refetches only the root, because
    // mode=promote_children moves children to a parent this component may not have loaded. Costs one
    // request and collapses the tree. Upgrade path if that ever annoys anyone: refetch the deleted
    // folder's parent and its grandparent instead of resetting.
    remove: (id, mode) =>
      run(async () => {
        await api.del(`/api/v1/folders/${id}?mode=${mode}`)
        setChildren({})
        setExpanded({})
        await load(null)
      }),
  }
}

export default function FolderTree({
  tree,
  currentId,
}: {
  tree: FolderTreeState
  currentId: string | null
}) {
  const [name, setName] = useState('')

  return (
    <nav className="folder-tree" data-testid="folder-tree" aria-label="Folders">
      <h3>Folders</h3>
      <ul>
        {(tree.childrenOf(null) ?? []).map((f) => (
          <Node key={f.id} folder={f} tree={tree} currentId={currentId} />
        ))}
      </ul>

      {/* Creates inside the folder you are looking at, so there is one form instead of one per node. */}
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault()
          if (!name.trim()) return
          void tree.create(name.trim(), currentId).then((ok) => ok && setName(''))
        }}
      >
        <label htmlFor="new-folder">Folder name</label>
        <input id="new-folder" value={name} onChange={(e) => setName(e.target.value)} />
        <button type="submit">Create folder</button>
      </form>

      {tree.error && (
        <p role="alert" className="error">
          {tree.error}
        </p>
      )}
    </nav>
  )
}

function Node({
  folder,
  tree,
  currentId,
}: {
  folder: Folder
  tree: FolderTreeState
  currentId: string | null
}) {
  const [mode, setMode] = useState<'idle' | 'rename' | 'confirm'>('idle')
  const [name, setName] = useState(folder.name)
  const navigate = useNavigate()
  const open = tree.isExpanded(folder.id)
  const kids = tree.childrenOf(folder.id)

  const del = (how: DeleteMode) =>
    void tree.remove(folder.id, how).then((ok) => {
      setMode('idle')
      // The folder you were looking at just stopped existing; its documents list would filter on a
      // dead id.
      if (ok && currentId === folder.id) void navigate('/')
    })

  return (
    <li data-testid="folder-node" data-name={folder.name}>
      <div data-testid="folder-row">
        <button
          type="button"
          className="link"
          aria-expanded={open}
          aria-label={`${open ? 'Collapse' : 'Expand'} ${folder.name}`}
          onClick={() => tree.toggle(folder.id)}
        >
          {open ? '▾' : '▸'}
        </button>
        <Link
          to={`/folders/${folder.id}`}
          aria-current={currentId === folder.id ? 'page' : undefined}
          onClick={() => tree.expand(folder.id)}
        >
          {folder.name}
        </Link>
        <button
          type="button"
          className="link"
          onClick={() => {
            setName(folder.name)
            setMode('rename')
          }}
        >
          Rename
        </button>
        <button type="button" className="link" onClick={() => setMode('confirm')}>
          Delete
        </button>
      </div>

      {mode === 'rename' && (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault()
            if (!name.trim()) return
            void tree
              .rename(folder.id, name.trim(), folder.parentId)
              .then((ok) => ok && setMode('idle'))
          }}
        >
          <label htmlFor={`rename-${folder.id}`}>New name</label>
          <input
            id={`rename-${folder.id}`}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <button type="submit">Save</button>
          <button type="button" className="link" onClick={() => setMode('idle')}>
            Cancel
          </button>
        </form>
      )}

      {/* DELETE /folders/{id}?mode= takes trash | promote_children, and 400s when it is omitted for a
          folder with children — so the choice is always explicit rather than defaulted. */}
      {mode === 'confirm' && (
        <p className="confirm">
          <button type="button" onClick={() => del('promote_children')}>
            Delete folder, keep contents
          </button>
          <button type="button" onClick={() => del('trash')}>
            Delete folder and contents
          </button>
          <button type="button" className="link" onClick={() => setMode('idle')}>
            Cancel
          </button>
        </p>
      )}

      {open && kids && kids.length > 0 && (
        <ul>
          {kids.map((k) => (
            <Node key={k.id} folder={k} tree={tree} currentId={currentId} />
          ))}
        </ul>
      )}
    </li>
  )
}

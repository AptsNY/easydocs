import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'

// Collabora's PostMessage API (https://sdk.collaboraonline.com/docs/postmessage_api.html), host side.
// Every message both ways is a JSON string {MessageId, SendTime, Values}. Collabora only talks to a page
// whose origin matches the PostMessageOrigin in CheckFileInfo, and this page only listens to the frame's
// own window at Collabora's origin.
//
// No "upload finished" signal exists: Action_Save_Resp arrives when the core has saved its local copy,
// before coolwsd uploads it to the WOPI host. So nothing here ever closes the edit session; see the
// spec's "Sessions are not closed from the page".
export type EditorStatus = 'loading' | 'saved' | 'unsaved' | 'saving' | 'failed'
export type SaveResult = 'done' | 'failed' | 'unknown'

type Message = { MessageId?: string; Values?: Record<string, unknown> }

const SAVE_TIMEOUT_MS = 15_000
// With PostMessageOrigin unset (or under the Vite dev server, a different origin) no message ever
// arrives while the editor works fine, so silence is not an error: the status is just hidden.
const QUIET_MS = 30_000

export function useCollabora(frame: RefObject<HTMLIFrameElement | null>, origin: string | null) {
  const [status, setStatus] = useState<EditorStatus>('loading')
  const [heard, setHeard] = useState(false)
  const [quiet, setQuiet] = useState(false)
  const loaded = useRef(false)
  const pendingSave = useRef<((ok: boolean) => void) | null>(null)

  const post = useCallback(
    (MessageId: string, Values: Record<string, unknown> = {}) => {
      if (origin) frame.current?.contentWindow?.postMessage(JSON.stringify({ MessageId, SendTime: Date.now(), Values }), origin)
    },
    [frame, origin],
  )

  useEffect(() => {
    if (!origin) return
    const onMessage = (e: MessageEvent) => {
      if (e.origin !== origin || e.source !== frame.current?.contentWindow) return
      let msg: Message
      try {
        msg = (typeof e.data === 'string' ? JSON.parse(e.data) : e.data) as Message
      } catch {
        return
      }
      setHeard(true)
      const v = msg.Values ?? {}
      switch (msg.MessageId) {
        case 'App_LoadingStatus':
          // Frame_Ready is Collabora's "I am listening"; the iframe load event can fire before that.
          if (v.Status === 'Frame_Ready') post('Host_PostmessageReady')
          if (v.Status === 'Document_Loaded') {
            loaded.current = true
            setStatus('saved')
          }
          break
        case 'Doc_ModifiedStatus':
          setStatus(v.Modified ? 'unsaved' : 'saved')
          break
        case 'Action_Save_Resp':
          // "unmodified" is Collabora declining a save there was no need for (DontSaveIfUnmodified).
          pendingSave.current?.(v.success === true || v.result === 'unmodified')
          pendingSave.current = null
          break
      }
    }
    window.addEventListener('message', onMessage)
    const t = window.setTimeout(() => setQuiet(true), QUIET_MS)
    return () => {
      window.removeEventListener('message', onMessage)
      window.clearTimeout(t)
    }
  }, [frame, origin, post])

  // Before Document_Loaded Collabora ignores Action_Save, and without PostMessageOrigin it never answers,
  // so waiting would only stall Done. 'unknown' means "leave anyway": the session stays open, so
  // Collabora's own save on disconnect still reaches the WOPI host.
  const save = useCallback((): Promise<SaveResult> => {
    if (!loaded.current) return Promise.resolve('unknown')
    setStatus('saving')
    return new Promise((resolve) => {
      const t = window.setTimeout(() => {
        pendingSave.current = null
        resolve('unknown')
      }, SAVE_TIMEOUT_MS)
      pendingSave.current = (ok) => {
        window.clearTimeout(t)
        if (!ok) setStatus('failed')
        resolve(ok ? 'done' : 'failed')
      }
      // DontSaveIfUnmodified: an unchanged document re-saved would become a new version on every Done.
      post('Action_Save', { Notify: true, DontSaveIfUnmodified: true })
    })
  }, [post])

  // The browser's own "Leave site?" prompt. Leaving anyway is safe: see save() above.
  useEffect(() => {
    if (status !== 'unsaved' && status !== 'saving') return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [status])

  return { status, hidden: quiet && !heard, save }
}

import { useEffect, useState } from 'react'
import { api } from './api'

// One GET, refetched when `tick` changes; a null path waits. Failure is local to the caller: each rail
// section fails on its own and editing is never blocked.
export function useRead<T>(path: string | null, tick = 0) {
  const [state, setState] = useState<{ data?: T; failed?: boolean }>({})
  useEffect(() => {
    if (!path) return
    let live = true
    api.get<T>(path).then(
      (data) => live && setState({ data }),
      () => live && setState({ failed: true }),
    )
    return () => {
      live = false
    }
  }, [path, tick])
  return state
}

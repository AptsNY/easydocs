import { useEffect, useId, useRef, type ReactNode } from 'react'

// ponytail: a native <dialog> opened with showModal(), so the focus trap, the top layer, the backdrop and
// Escape-to-dismiss are the platform's job — no focus-trap dependency, no keydown bookkeeping to get
// wrong. Ceiling: the browser restores focus to whatever was focused when showModal() ran, which here is
// a menu item the caller unmounts on the way in, so ActionsMenu re-focuses its own trigger on close.
// Upgrade path if a screen ever needs a non-modal or stacked dialog: that is what <dialog>.show() is for.
export default function Modal({
  title,
  onClose,
  children,
}: {
  title: string
  onClose: () => void
  children: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()

  useEffect(() => {
    // Guarded because StrictMode runs effects twice in development and older engines threw on a second
    // showModal(); the current spec makes it a no-op.
    if (!ref.current?.open) ref.current?.showModal()
  }, [])

  // onClose covers both paths: Escape fires `cancel` and then `close`, and close() from a button fires
  // `close` — one handler, no duplicate dismissals.
  return (
    <dialog className="modal" ref={ref} aria-labelledby={titleId} onClose={onClose}>
      <h3 id={titleId}>{title}</h3>
      {children}
    </dialog>
  )
}

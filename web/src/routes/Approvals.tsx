// Task 16 fills this in. Two mounts: the org-wide inbox at /approvals, and one document's tab.
export default function Approvals({ inbox = false }: { inbox?: boolean }) {
  return (
    <div data-testid={inbox ? 'approvals-inbox' : 'approvals'}>
      <h3>{inbox ? 'My approvals' : 'Approvals'}</h3>
    </div>
  )
}

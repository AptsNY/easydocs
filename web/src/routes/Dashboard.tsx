// Task 11 fills this in: folder tree, document tiles, search, trash.
export default function Dashboard({ trashed = false }: { trashed?: boolean }) {
  return (
    <section data-testid="dashboard">
      <h2>{trashed ? 'Trash' : 'Documents'}</h2>
    </section>
  )
}

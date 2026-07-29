import { NavLink, Outlet, useParams } from 'react-router'

// Task 12 fills this in: version list, Actions menu, members panel. The tabs are real routes now so the
// route table is verifiable before any of them has content.
export default function DocumentConsole() {
  const { id } = useParams()
  return (
    <section data-testid="document-console">
      <h2>Document</h2>
      <nav>
        <NavLink to={`/documents/${id}`} end>
          History
        </NavLink>
        <NavLink to={`/documents/${id}/major-versions`}>Major Versions</NavLink>
        <NavLink to={`/documents/${id}/copies`}>Copies</NavLink>
        <NavLink to={`/documents/${id}/approvals`}>Approvals</NavLink>
        <NavLink to={`/documents/${id}/audit`}>Audit</NavLink>
      </nav>
      <Outlet />
    </section>
  )
}

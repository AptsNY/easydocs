import { useOutletContext, useParams } from 'react-router'
import MembersPanel from '../components/MembersPanel'

// The Members tab. The roster used to sit beside every console tab, which left History (now the version
// page, with real pages beside the list) a third of the width; it has its own tab instead.
export default function Members() {
  const { id } = useParams()
  const { tick } = useOutletContext<{ tick: number }>()
  return <MembersPanel documentId={id} tick={tick} />
}

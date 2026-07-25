import { useEffect, useState } from 'react'

export default function App() {
  const [status, setStatus] = useState('loading...')

  useEffect(() => {
    fetch('/health')
      .then((r) => r.json())
      .then((d) => setStatus(d.status))
      .catch(() => setStatus('error'))
  }, [])

  return (
    <main>
      <h1>easydocs</h1>
      <p>API health: {status}</p>
    </main>
  )
}

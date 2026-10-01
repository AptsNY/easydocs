// Production heartbeat: one pass through what a person relies on, every hour, as one dedicated account.
// API calls only (no browser): fast, nothing to scrape, and each step names the part of the stack it
// proves. Exits non-zero naming the first step that failed; heartbeat.yml turns that into an issue.
//
//   HEARTBEAT_BASE_URL=https://easydocs.aptsny.net HEARTBEAT_EMAIL=… HEARTBEAT_PASSWORD=… node heartbeat.mjs
//
// The account owns one document, "Heartbeat", with two versions. If it is missing (first run, or someone
// trashed it) the script seeds it from the e2e fixtures, so the heartbeat never needs hand setup.
import { readFileSync } from 'node:fs'

const BASE = (process.env.HEARTBEAT_BASE_URL ?? 'https://easydocs.aptsny.net').replace(/\/$/, '')
const { HEARTBEAT_EMAIL: email, HEARTBEAT_PASSWORD: password } = process.env
const FIXTURES = new URL('../../web/e2e/fixtures/', import.meta.url)
const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
const DOC_NAME = 'Heartbeat'

// Not configured yet is not an outage: warn and pass, so an unset secret never opens hourly issues.
if (!email || !password) {
  console.log('::warning::heartbeat not configured: set the HEARTBEAT_EMAIL and HEARTBEAT_PASSWORD secrets')
  process.exit(0)
}

let auth = {}
const results = []

async function step(name, fn) {
  const t0 = Date.now()
  try {
    const detail = await fn()
    results.push(`ok   ${name} (${Date.now() - t0} ms)${detail ? ` — ${detail}` : ''}`)
  } catch (e) {
    results.push(`FAIL ${name} (${Date.now() - t0} ms) — ${e.message}`)
    console.log(results.join('\n'))
    process.exit(1)
  }
}

async function call(method, path, { body, form, expect = 200, raw = false } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: { ...auth, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: form ?? (body ? JSON.stringify(body) : undefined),
    signal: AbortSignal.timeout(90_000),
  })
  if (res.status !== expect) throw new Error(`${method} ${path.split('?')[0]} answered ${res.status}: ${(await res.text()).slice(0, 200)}`)
  return raw ? res : res.status === 204 ? null : res.json()
}

const upload = (docId, file) => {
  const form = new FormData()
  form.append('file', new Blob([readFileSync(new URL(file, FIXTURES))], { type: DOCX }), file)
  return call('POST', `/api/v1/documents/${docId}/versions`, { form, expect: 201 })
}

await step('app is up and its database answers (/health/ready)', async () => void (await call('GET', '/health/ready', { raw: true })))

await step('sign in', async () => {
  const res = await call('POST', '/api/v1/auth/login', { body: { email, password }, raw: true })
  const body = await res.json()
  if (body.mfaRequired) throw new Error('the heartbeat account has MFA on; it must not')
  const cookie = res.headers.getSetCookie().find((c) => c.startsWith('ed_session='))
  if (!cookie) throw new Error('no session cookie in the login response')
  auth = { Authorization: `Bearer ${cookie.slice('ed_session='.length).split(';')[0]}` }
})

let docId
// Seeds whatever is missing — the document, or versions someone removed — so a half-finished seed or a
// trashed version heals on the next run instead of failing every hour.
await step('find (or seed) the heartbeat document', async () => {
  const page = await call('GET', `/api/v1/documents?q=${encodeURIComponent(DOC_NAME)}&limit=10`)
  docId = page.items.find((d) => d.name === DOC_NAME)?.id
  let seeded = 0
  if (!docId) {
    docId = (await call('POST', '/api/v1/documents', { body: { name: DOC_NAME }, expect: 201 })).id
    seeded++
  }
  const { items } = await call('GET', `/api/v1/documents/${docId}/versions?limit=2`)
  for (const file of ['base.docx', 'edited.docx'].slice(items.length)) {
    await upload(docId, file)
    seeded++
  }
  return seeded ? `seeded ${seeded} item(s)` : 'found'
})

let from, to
await step('list its versions', async () => {
  const { items } = await call('GET', `/api/v1/documents/${docId}/versions?order=desc&limit=2`)
  if (items.length < 2) throw new Error(`expected 2 versions, found ${items.length}`)
  ;[to, from] = [items[0].id, items[1].id]
})

await step('redline renders as pages (comparer + Gotenberg)', async () => {
  const res = await call('GET', `/api/v1/documents/${docId}/compare?from=${from}&to=${to}&format=pdf`, { raw: true })
  const head = Buffer.from(await res.arrayBuffer()).subarray(0, 5).toString()
  if (head !== '%PDF-') throw new Error(`not a PDF (starts "${head}")`)
})

await step('editor session opens and Collabora answers', async () => {
  const s = await call('POST', `/api/v1/versions/${to}/sessions`, { expect: 201 })
  try {
    const discovery = await fetch(`${new URL(s.editorUrl).origin}/hosting/discovery`, { signal: AbortSignal.timeout(30_000) })
    if (!discovery.ok) throw new Error(`Collabora discovery answered ${discovery.status}`)
  } finally {
    // Nothing is editing through this session, so closing it cannot race an upload.
    await call('DELETE', `/api/v1/sessions/${s.sessionId}`, { expect: 204 }).catch(() => {})
  }
})

console.log(results.join('\n'))

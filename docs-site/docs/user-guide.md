# User guide

Task-by-task instructions for the web UI, screen by screen, and an orientation to the REST API.
This page assumes easydocs is already running — if it isn't, start with
[Getting started](getting-started.md). For *why* things behave the way they do (numbering, branches,
redlines), see [Concepts](concepts.md).

Throughout: what you can do depends on your **role on the document** — *Viewer* (read, compare,
download, share), *Editor* (everything Viewers do, plus edit, import, publish, revert, push), or
*Owner* (everything, plus approvals and membership).

## Signing in

- **First account:** registering creates your user *and* your organization, and makes you its owner.
- **Password sign-in:** email + password. If your account has **MFA enabled**, a second screen asks
  for the six-digit code from your authenticator app — a recovery code works there too.
- **SSO:** if your install has an OIDC provider configured, the sign-in screen shows
  **Sign in with SSO**. First-time SSO users get an account and their own organization
  automatically.
- **Joining someone else's organization:** you don't sign up into it — you follow an **invitation
  link** someone sends you (see [Members](#members-and-the-audit-trail)). If you belong to more than
  one organization, a **switcher appears in the header**; a session is always scoped to exactly one.

## The dashboard

The dashboard is folders on the left, documents on the right.

- **Create a folder** to group documents; folders nest freely, and moving a document never touches
  its history or members.
- **Create document** makes an empty document; its first upload becomes version `0.0.1`.
- **Import a document** does both at once: pick a `.docx` and you get a document already holding it as
  version `0.0.1`. The name comes from the file, and you can change it before importing.
- **Search names & content** — the one search box matches document *names* and, since v1.1, the
  *text inside* their current versions. Phrases (`"exact phrase"`) and exclusions (`word -other`)
  work. Content lands in the index a few seconds after a save.
- **Sort** — documents arrive most-recently-updated first. The sort menu also offers name (A–Z or
  Z–A) and creation date, either direction. Your choice is part of the page's address, so it survives
  a reload and can be shared as a link.
- **Trash** — deleting a document moves it to the trash view, from which it can be restored.
  Nothing in the version history is destroyed by trashing.

## The document console

Opening a document lands on its console: tabs for **Documents · History · Major Versions ·
Copies · Approvals · Audit**, plus the **Members** panel.

### History

Every version is a row: number (`X.Y.Z`), name, author, timestamp, source (upload, browser edit,
Word edit, merge, revert, push), and a change summary ("3 insertions, 1 deletion") computed against
its parent. The console updates **live** — a colleague's save appears without reloading.

- **List / Graph toggle:** the list groups concurrent branches under the version they forked from;
  the graph draws the same history as lanes and dots, one lane per branch, with merge edges.
- **Concurrent branches:** when two people save from the same starting version, the second save
  becomes a *branch* instead of overwriting — it appears indented (or as its own lane) with a
  **Merge** button. Merging lands a new version on the main line with the incoming author's changes
  as tracked changes. Nothing is ever discarded, so there is no confirmation step.

### The Actions menu

Each version row has an **Actions** menu. What you see is role-filtered:

| Action | Role | What it does |
|---|---|---|
| **Open in Collabora** | Editor | Edits in the browser. Saving (or closing the editor) commits a new version automatically. |
| **Open in Word** | Editor | Hands the document to *desktop* Microsoft Word via an `ms-word:` link. Saving in Word commits a new version, exactly like a browser save. Needs Word installed; the link expires after 30 minutes. |
| **Import** | Editor | Uploads a `.docx` from disk as the next version — for edits that happened outside easydocs. |
| **Share** | Viewer | Creates and manages share links for this version (see [Sharing](#sharing)). |
| **Download** | Viewer | Downloads the version; `?format=pdf` variants exist for published versions with a rendered PDF. |
| **Name** | Editor | Gives the version a human name ("Board draft"), shown alongside its number. |
| **Publish** | Editor | Publishes as minor or major (see below). |
| **Revert** | Editor | Commits the *old* version's content as a **new** head. History is never rewritten — a revert is a new version whose bytes are the old ones. |
| **Push To Copy** | Editor | Forks this version into a client copy (see [Copies](#copies-and-push-back)). |

### Comparing versions

**Compare versions** (top of the History tab) opens the comparison view: pick any two versions and
get a real **redline** — insertions and deletions computed from the documents themselves, whether
or not anyone ever turned Track Changes on. The numeric summary on each history row is the same
comparison, run automatically against the version's parent.

### Publishing and Major Versions

Drafts live at `0.0.Z`. **Publish** renumbers the chosen version from the document's counter —
**minor** (`X.Y+1.0`) or **major** (`X+1.0.0`) — stamps who/when, optionally names it, and renders a
**PDF** in the background (a version that already *is* a PDF is published byte-identical, never
re-rendered). The **Major Versions** tab lists everything published, with the PDFs.

### Approvals

On a published version, an Owner can **Request approval** from named document members, optionally
with a due date. Each approver gets exactly one immutable decision — approve or reject, with a
comment. The **Approvals** screen (in the header) gathers everything **Asked of me** and **Asked by
me**, filterable by status. A pending request can be cancelled while open; a decision, never.

### Copies and push-back

**Push To Copy** forks a version into an isolated *copy* — its own document, own members, own
history — for the "send it to the client's lawyers" workflow. When the copy's people finish, they
use **Send back** on the copy's **Copies → Pushes** tab. A member of the original then reviews the
push: **accept** lands it as a clearly-labelled incoming branch (mergeable like any branch);
**reject** and it never enters the history. Either way the original's history only ever gains
clearly-attributed versions.

### Sharing

**Share** on a version creates a link scoped to *that version*: optional **expiry**, revocable any
time, and audited — every anonymous view lands in the audit trail with a view count. The recipient
needs no account and sees a plain download page: no app chrome, no sign-up wall. Existing links for
the document are listed in the same dialog, each with its revoke button.

### Members and the audit trail

The **Members** panel governs access to *this document*: add someone by email (an existing user, or
**Add member** issues an invitation link for a newcomer — sending it to them is on you; easydocs
sends no email), set their role, remove them. Organization roles (Settings) govern only the
Settings screen — document access is always granted per document.

The **Audit** tab is the append-only trail: uploads, edits, publishes, approvals, shares and views,
membership changes — who, what, when.

## Settings

- **Profile** — your name and email.
- **Two-factor authentication** — **Set up** shows a secret to add to any authenticator app
  (Google Authenticator, 1Password, Aegis…); confirm with one code and MFA is on. **Save the ten
  recovery codes immediately — they are shown exactly once.** Each works one time if you lose the
  authenticator. Turning MFA off requires a current code.
- **API tokens** — mint `ed_` personal access tokens for scripts and integrations. The value is
  shown once, at creation. A token acts as you, in the current organization, and can never exceed
  your role.
- **Organization** — rename it, and manage **organization members** and their org roles. Inviting
  someone here (or from a document's Members panel) produces the invitation link you send them.

## The API

Everything the UI does, the API does — same surface, not a subset. Three things to know:

1. **The reference lives in your install**, generated from the running build so it can never be
   stale: interactive docs at `/docs`, the OpenAPI 3.1 document at `/openapi/v1.json`.
2. **Authenticate** with an `ed_` token (`Authorization: Bearer ed_…`) from Settings → API tokens,
   or the `ed_session` cookie if you're scripting alongside a browser session.
3. **Live updates** are server-sent events, per document, at `/api/v1/documents/{id}/events` —
   the same stream the UI itself uses.

For copy-paste worked examples of the whole lifecycle — create, upload, edit, compare, publish,
approve, share — see [Automation recipes](automation-recipes.md).

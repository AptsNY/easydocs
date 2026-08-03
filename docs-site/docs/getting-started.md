# Getting started

This takes one `.docx` from nothing to two numbered versions, editing in the browser. Budget about ten
minutes, most of it the first Docker build (LibreOffice is bundled into the image, which is a large
install).

## What you need

- **Docker** with Compose. Nothing else — no .NET SDK, no Node, no Word.
- About 3 GB of disk for the images.
- Ports **8080** (the app) and **9980** (Collabora) free on the host.

## 1. Clone and configure

```bash
git clone https://github.com/AptsNY/easydocs
cd easydocs/deploy/compose
cp .env.example .env
```

Now **edit `.env`**. Two values ship as placeholders and must be changed:

```bash
POSTGRES_PASSWORD=change-me
Jwt__Secret=replace-with-a-long-random-secret-at-least-32-bytes
```

`Jwt__Secret` signs session cookies and WOPI tokens. It **must be at least 32 bytes** — HS256 requires
a 256-bit key, and the application refuses to start otherwise, with this message:

```
Jwt:Secret must be configured and at least 32 bytes (HS256 requires a 256-bit key).
```

That is deliberate: a short signing key failing at boot is far better than failing at the first login.
Generate one:

```bash
python3 -c 'import secrets; print(secrets.token_urlsafe(48))'
# or: openssl rand -base64 48
```

`POSTGRES_PASSWORD` appears **twice** — once on its own and once inside
`ConnectionStrings__Postgres`. Change both, to the same value, or the app cannot reach its database.

!!! warning "Leave `PUBLIC_BASE_URL` alone until you have TLS"
    The default `PUBLIC_BASE_URL=http://localhost:8080` works because browsers treat `localhost` as a
    secure context. Serving easydocs over plain HTTP on a LAN IP or an internal hostname will make
    login **appear to succeed and then silently fail** — the session cookie is `Secure`, so the browser
    discards it. See [Self-hosting → TLS is mandatory in practice](self-hosting.md#tls-is-mandatory-in-practice).

## 2. Bring it up

```bash
docker compose up --build
```

Three containers start: `easydocs` (the app, with LibreOffice bundled for PDF rendering), `postgres`,
and `collabora`. Database migrations are applied automatically at startup — there is no separate
migrate step to run, ever.

Wait for the app to answer:

```bash
curl http://localhost:8080/health
# {"status":"ok"}
```

## 3. Register — this also creates your organization

Open **<http://localhost:8080>** and register.

The first account you create also creates your **organization**, and you become its owner. This is by
design: every query in easydocs is filtered by organization, so a user without one would be able to see
nothing at all.

!!! note "A session carries exactly one organization"
    There is no org switcher in v1. If you later accept an invitation to a different organization, your
    session rebinds to the inviting one. See [Concepts](concepts.md#organizations-and-membership).

## 4. Create a folder

From the dashboard, create a folder. Folders nest as deep as you like and are the only structure
easydocs imposes — moving a document between folders never touches its history or its members.

## 5. Upload a `.docx`

Create a document in the folder and upload a `.docx`. The first version is **exactly `0.0.1`** — not
`1.0.0`. Everything before you publish is a draft, and drafts live in the `0.0.Z` range until you
decide otherwise.

The document console now shows one version row: number, author, timestamp, and a change summary. The
summary for a first version is empty, because there is no parent to compare against.

## 6. Open it in Collabora, edit, and save

Open the version's **Actions** menu and choose **Open in Collabora**. The document opens in a real
Word-compatible editor in the browser — no upload/download dance, no Word install.

Type something. Then **save from inside Collabora** (Ctrl/Cmd-S, or just close the editor — easydocs
commits on session close).

## 7. Watch the new version appear

Back on the document console, a second row appears: **`0.0.2`**, attributed to you, with a change
summary like "3 insertions, 1 deletion". You did not create it. That is the whole point — versioning is
invisible and automatic.

The console updates live over server-sent events, so you should see the row arrive without reloading.
The change summary may land a moment after the row itself; it is computed by a background worker.

!!! tip "Saving without changing anything creates nothing"
    Blobs are content-addressed by sha256, and the save path de-duplicates on it. Collabora re-saving
    an unchanged document is a no-op, so you never get version spam from an idle editor.

## Where next

- **[Concepts](concepts.md)** — what `0.0.2` actually means, what happens when two people edit at once,
  and how publishing renumbers things. Read this before you invite anyone.
- **[Self-hosting](self-hosting.md)** — before you put this anywhere but your laptop. TLS, reverse
  proxies, backups.
- **[Automation recipes](automation-recipes.md)** — the same lifecycle over the REST API.

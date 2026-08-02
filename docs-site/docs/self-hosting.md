# Self-hosting

The operator's guide. If you read one page before putting easydocs in front of colleagues, read this
one — particularly [TLS](#tls-is-mandatory-in-practice), [forwarded
headers](#forwarded-headers-behind-a-proxy), [backups](#backup-and-restore), and the [logging
warning](#never-raise-the-aspnet-core-log-level-in-production).

## The stack

```bash
cd easydocs/deploy/compose
cp .env.example .env      # then edit the secrets — see below
docker compose up --build
```

Three services:

| Service | Image | What it is |
|---|---|---|
| **`easydocs`** | built from `Dockerfile` | The whole application: REST API, the React SPA served from `wwwroot`, the WOPI host, and in-process background workers. LibreOffice is bundled into the image for PDF rendering. Listens on **8080**. |
| **`postgres`** | `postgres:16` | Metadata, ACLs, audit trail. Not the document contents. |
| **`collabora`** | `collabora/code` | Collabora Online, for in-browser `.docx` editing over WOPI. Listens on **9980**. |

Two named volumes, both of which matter:

- **`compose_pgdata`** → `/var/lib/postgresql/data`. The database.
- **`compose_blobs`** → `/data/blobs` in the app container. **The document contents**, content-addressed
  by sha256. This is where every version of every `.docx` actually lives.

(Volume names are prefixed with the Compose project name, which defaults to the directory name —
`compose`. `docker volume ls` will confirm yours.)

### Migrations are automatic

EF Core migrations are applied on application startup, every start. There is no migrate step to run and
no `dotnet ef` to install. A fresh database is created and migrated by the first boot.

The practical consequence: **take a database backup before upgrading the image**, because the new
container will migrate your schema the moment it starts. See [Backup and restore](#backup-and-restore).

## `.env` reference

Every variable in `deploy/compose/.env.example`.

| Variable | Secret? | What it does |
|---|---|---|
| `POSTGRES_USER` | no | Postgres role the database is created for. Default `easydocs`. |
| `POSTGRES_PASSWORD` | **yes** | Password for that role. Ships as `change-me`. **Change it.** |
| `POSTGRES_DB` | no | Database name. Default `easydocs`. |
| `ConnectionStrings__Postgres` | **yes** (contains the password) | The app's connection string. **Contains `POSTGRES_PASSWORD` a second time** — change it in both places, to the same value, or the app cannot connect. |
| `BLOB_ROOT` | no | Where blobs are written inside the container. `/data/blobs`, matching the volume mount. The app refuses to start if this is unset. |
| `Jwt__Secret` | **yes** | HS256 signing key for session cookies **and** WOPI access tokens. **Must be ≥ 32 bytes.** See below. |
| `PUBLIC_BASE_URL` | no | The URL **browsers** use to reach easydocs, e.g. `https://docs.example.com`. Also allowlisted into Collabora's `aliasgroup1`. Get this wrong and the editor iframe will not load. |
| `WOPI_HOST_URL` | no | The URL **Collabora** uses to reach easydocs, from inside the Docker network — `http://easydocs:8080`. Not the public URL. This is why there are two. |
| `COLLABORA_URL` | no | Where the app reaches Collabora to fetch its discovery document — `http://collabora:9980`. |
| `RateLimit__*` | no | Rate-limit overrides, all commented out by default. See [Rate limiting](#rate-limiting). |

Everything uses ASP.NET's double-underscore convention: `Jwt__Secret` in the environment is the
`Jwt:Secret` configuration key. Any configuration key can be set this way, whether or not it appears in
`.env.example`.

### `Jwt__Secret` must be at least 32 bytes

HS256 requires a 256-bit key. If `Jwt:Secret` is missing or shorter than 32 bytes, **the application
throws at boot**:

```
Jwt:Secret must be configured and at least 32 bytes (HS256 requires a 256-bit key).
```

This is deliberate. The alternative — starting successfully and failing on the first login, or worse,
signing tokens with a weak key — is strictly harder to diagnose and strictly less safe. Generate one:

```bash
python3 -c 'import secrets; print(secrets.token_urlsafe(48))'
# or: openssl rand -base64 48
```

!!! danger "Rotating `Jwt__Secret` invalidates every session and every open editor"
    It signs session cookies and WOPI tokens. Change it and everyone is logged out, and any Collabora
    editing session open at that moment fails. Do it during a quiet window. It does **not** affect
    stored passwords, `ed_` API tokens, share links, or invitations — none of those are derived from it.

### `PUBLIC_BASE_URL` vs `WOPI_HOST_URL`

The most common wiring mistake. The browser and the Collabora container see easydocs at **different
addresses**, so both must be configured:

```
Browser ──── PUBLIC_BASE_URL (https://docs.example.com) ────► easydocs
   │
   └──────── PUBLIC_BASE_URL ────► Collabora editor iframe
                                       │
                                       └── WOPI_HOST_URL (http://easydocs:8080) ──► easydocs
                                           (CheckFileInfo / GetFile / PutFile, inside the network)
```

`PUBLIC_BASE_URL` is also passed to Collabora as `aliasgroup1`, which is Collabora's host allowlist. If
it does not match the host the browser actually used, Collabora refuses the request.

## TLS is mandatory in practice

Not "recommended". **The session cookie is `Secure`**, which means a browser will only ever send it back
over HTTPS — or to `localhost`.

`http://localhost:8080` works because browsers treat localhost as a secure context. That is the *only*
plain-HTTP case that works.

!!! danger "The failure mode is silent and looks like a bug in easydocs"
    Serve easydocs over plain HTTP on a LAN IP or an internal hostname —
    `http://192.168.1.50:8080`, `http://easydocs.internal` — and **login will appear to succeed and then
    silently fail**. The API authenticates the user and sets the cookie; the browser discards it because
    the connection is not secure; the next request is unauthenticated and the user lands back on the
    sign-in screen. There is no error message, because from the server's point of view nothing went
    wrong.

    Every "I can log in but it just bounces me back" report is this.

Terminate TLS at a reverse proxy in front of the app. Dropping `Secure` from the cookie is **not** a
supported workaround — it would hand every session cookie to anyone who can watch the network.

### Caddy

Two lines, and it obtains and renews a certificate itself. Start here.

```caddy
# /etc/caddy/Caddyfile
docs.example.com {
	reverse_proxy localhost:8080
}
```

Caddy sets `X-Forwarded-For` and `X-Forwarded-Proto` correctly by default, so you only need to tell
easydocs to trust them — see [forwarded headers](#forwarded-headers-behind-a-proxy).

Server-sent events (the live-updating document console) need response buffering off. Caddy does not
buffer by default, so there is nothing to do. If you have added `flush_interval`, set it to `-1`.

### nginx

```nginx
server {
    listen 443 ssl http2;
    server_name docs.example.com;

    ssl_certificate     /etc/letsencrypt/live/docs.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/docs.example.com/privkey.pem;

    # .docx uploads. Raise to suit your largest document; the default 1m is too small.
    client_max_body_size 200m;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Server-sent events: the document console streams from
        # /api/v1/documents/{id}/events and must not be buffered.
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
    }
}

server {
    listen 80;
    server_name docs.example.com;
    return 301 https://$host$request_uri;
}
```

Then set `PUBLIC_BASE_URL=https://docs.example.com` in `.env` and restart the stack.

!!! note "Collabora behind the same proxy"
    The compose file runs Collabora with `--o:ssl.enable=false --o:ssl.termination=true`: it speaks
    plain HTTP inside the Docker network and expects TLS to be terminated at the edge. If you expose
    Collabora publicly (port 9980), proxy it with TLS as well, using a WebSocket-capable configuration.
    The default compose setup keeps app↔Collabora traffic on the internal network, which is the simpler
    and safer arrangement.

## Forwarded headers behind a proxy

Behind a reverse proxy, **set this**:

```bash
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

Without it, `RemoteIpAddress` is **the proxy's address on every request**. Two things break:

1. **Per-IP rate limits become install-wide.** Every caller collapses onto one partition key, so the
   whole installation shares a single budget. Note that this **fails closed, not open**: the limits
   become stricter than intended, not looser. Nobody gets extra allowance; everybody shares one.
2. **The `ip` field in the share-view audit row is wrong on every public view.** It records the proxy's
   address rather than the recipient's, which quietly makes that part of the audit trail useless.

### Why it is not on by default

Because trusting `X-Forwarded-For` unconditionally would be a security hole. The header is client-
supplied. If the app believed it without knowing which hop is trustworthy, **anyone could bypass the
per-IP rate limiter** by sending a different `X-Forwarded-For` on each request — turning a protection
into an open door. Enabling it is a statement that a proxy you control is in front of the app and is
overwriting that header.

### What enabling it actually does — read this before you enable it

`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` is a framework switch, not an easydocs one, and it does two
things. ASP.NET Core's `ForwardedHeadersOptionsSetup` is the whole implementation:

```csharp
options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
// Only loopback proxies are allowed by default. Clear that restriction because forwarders are
// being enabled by explicit configuration.
options.KnownIPNetworks.Clear();
options.KnownProxies.Clear();
```

So it processes `X-Forwarded-For` and `X-Forwarded-Proto` — which is what fixes `RemoteIpAddress` — and
it **clears the loopback-only restriction, trusting the forwarded headers from any peer.**

!!! danger "Enabling this means the app must not be directly reachable"
    Because the trusted-proxy restriction is cleared, **anything that can open a TCP connection to
    easydocs can now dictate its own client IP** and get a fresh rate-limit budget per request. Behind a
    proxy that overwrites the header, that is fine. Directly reachable, it is a rate-limiter bypass.

    Bind the app's port to the proxy only. In `deploy/compose/docker-compose.yml`, change:

    ```yaml
    ports:
      - "8080:8080"          # listens on every interface — do not combine with forwarded headers
    ```

    to:

    ```yaml
    ports:
      - "127.0.0.1:8080:8080"   # reachable only by a proxy on this host
    ```

    If the proxy runs in another container, drop the `ports:` mapping entirely and put both on a shared
    Docker network, so the app is reachable only over that network.

### Narrowing trust to named proxies (v1.1+)

Since v1.1 the trusted-proxy lists bind from configuration, so you can name the proxy instead of
relying on port binding alone:

```bash
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ForwardedHeaders__KnownProxies__0=10.0.0.5        # exact proxy IPs…
ForwardedHeaders__KnownNetworks__0=172.16.0.0/12  # …or CIDR ranges (Docker networks live here)
```

With either list set, `X-Forwarded-*` headers are honored only from those addresses; a connection
from anywhere else is treated as the client itself. Both lists are additive — set as many indexed
entries as you have proxies.

Two properties worth knowing, both deliberate:

- **Misconfiguration aborts boot.** An entry that isn't an IP / CIDR, or lists set while
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is off (which would make them dead config), stops the app at
  startup with a message naming the problem. On easydocs v1.0 these keys were silently ignored —
  never again.
- **Network isolation is still worth keeping.** The port-binding advice above remains good
  defense-in-depth even with named proxies; the lists narrow header trust, not reachability.

### Verify it worked

Create a share link, open it from another machine, and check the document's audit trail. The `ip` in the
`share_link.viewed` row should be the visitor's address, not your proxy's.

## Rate limiting

Four named policies, applied per endpoint rather than globally. Static assets and the SPA shell are
deliberately **not** metered: serving `index.html` is not abuse, and a throttled asset load reads as an
outage rather than as a limit.

| Policy | Applies to | Default | Partitioned by |
|---|---|---|---|
| `anon-share` | `GET /s/{token}` | 120 per 60s, fixed window | client IP |
| `anon-download` | `GET /s/{token}/download` | 30 per 60s, fixed window | client IP |
| `auth` | `POST /api/v1/auth/login`, `POST /api/v1/auth/register` | token bucket: 1000 burst, +50 every 10s ⇒ **~300/min sustained** | request path **and** client IP, so login and register meter independently |
| `token-mint` | `POST /api/v1/tokens` | 20 per 60s, fixed window | authenticated **user id** |

Rejections are `429` with `problem+json` and a `Retry-After` header.

### Tuning

Every value is configurable under `RateLimit:<Policy>:*`. The exact keys, as read by the code in
`src/EasyDocs.Api/Common/RateLimits.cs`:

```bash
RateLimit__AnonShare__PermitLimit=120
RateLimit__AnonShare__WindowSeconds=60

RateLimit__AnonDownload__PermitLimit=30
RateLimit__AnonDownload__WindowSeconds=60

RateLimit__TokenMint__PermitLimit=20
RateLimit__TokenMint__WindowSeconds=60

RateLimit__Auth__BurstLimit=1000
RateLimit__Auth__TokensPerPeriod=50
RateLimit__Auth__ReplenishmentSeconds=10
```

When to change them:

- **Raise `AnonDownload__PermitLimit`** if a whole team opens one share link at once. 30/min of a
  multi-megabyte `.docx` is the egress cap, and it is the limit that will bite first in normal use.
- **Tighten `Auth__*`** if the install is public. The shipped defaults are a **flood brake, not an
  anti-stuffing control**: they stop one client burning unbounded Argon2id CPU or creating unbounded
  organizations. They do **not** stop patient or distributed credential stuffing — that is a WAF or
  fail2ban job at the proxy. The defaults are loose partly because a whole office behind one NAT must
  keep working when [forwarded headers](#forwarded-headers-behind-a-proxy) are not configured.
- **Remember `token-mint` is per user**, not per IP, so a shared proxy address does not collapse it.

## Backup and restore

!!! danger "Two things must be backed up together"
    The **Postgres database** and the **blob volume**. The database holds every version row, ACL, and
    audit event; the blob volume holds the actual `.docx` bytes. **A database without its blobs is
    useless** — you get a complete, browsable history in which every download 500s. Blobs without a
    database are an undifferentiated pile of sha256-named files.

    Back them up in the same run, and restore them as a pair.

### Backup

```bash
cd easydocs/deploy/compose
set -a; . ./.env; set +a
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p ~/easydocs-backups

# 1. The database.
docker compose exec -T postgres \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom \
  > ~/easydocs-backups/easydocs-db-$STAMP.dump

# 2. The blobs. Read the volume through a throwaway container.
docker run --rm \
  --volumes-from "$(docker compose ps -q easydocs)" \
  -v ~/easydocs-backups:/backup \
  alpine tar czf /backup/easydocs-blobs-$STAMP.tar.gz -C /data blobs
```

Both files together are a complete backup. Take them while the stack is running if you must — the
`pg_dump` is consistent, and blobs are immutable and content-addressed, so the only race is a version
written between the two commands, which restores as a database row whose blob is missing. If you need
a strictly consistent pair, stop the `easydocs` service first (leaving `postgres` up):

```bash
docker compose stop easydocs
# ... run both commands above ...
docker compose start easydocs
```

Verify a backup is not empty — an untested backup is a guess:

```bash
ls -lh ~/easydocs-backups/easydocs-{db,blobs}-$STAMP.*
docker run --rm -v ~/easydocs-backups:/b alpine \
  tar tzf /b/easydocs-blobs-$STAMP.tar.gz | head
```

### Restore

Into a fresh stack, with the app stopped so migrations do not race the restore:

```bash
cd easydocs/deploy/compose
set -a; . ./.env; set +a
STAMP=<the stamp you are restoring>

docker compose up -d postgres        # database only
docker compose stop easydocs 2>/dev/null || true

# 1. The database, into a clean one.
docker compose exec -T postgres \
  dropdb -U "$POSTGRES_USER" --if-exists "$POSTGRES_DB"
docker compose exec -T postgres \
  createdb -U "$POSTGRES_USER" "$POSTGRES_DB"
docker compose exec -T postgres \
  pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  < ~/easydocs-backups/easydocs-db-$STAMP.dump

# 2. The blobs, back into the volume.
docker compose create easydocs       # create the container so its volume exists
docker run --rm \
  --volumes-from "$(docker compose ps -aq easydocs)" \
  -v ~/easydocs-backups:/backup \
  alpine sh -c 'rm -rf /data/blobs && tar xzf /backup/easydocs-blobs-'"$STAMP"'.tar.gz -C /data'

docker compose up -d
```

Restore the **database first**, then the blobs, then start the app — its startup migration will bring
an older schema forward. Restoring a newer dump into an older image is not supported; migrations only
run forward.

Then check that a download actually works, not just that the app boots. Downloading an old version is
the only thing that proves both halves of the restore landed.

## Never raise the ASP.NET Core log level in production

!!! danger "This one leaks live credentials to stdout"
    The WOPI `access_token` travels **in the query string**, because that is what the WOPI protocol
    specifies. ASP.NET Core's request logging prints full request URLs at `Information` level.

    The **only** thing preventing live WOPI tokens from being written to your container logs is this,
    in `appsettings.json`:

    ```json
    "Logging": { "LogLevel": { "Microsoft.AspNetCore": "Warning" } }
    ```

    Setting **`Logging__LogLevel__Microsoft.AspNetCore=Information`** — or `Debug`, or `Trace` — **prints
    live WOPI access tokens to stdout**, where they land in your Docker logs, your log shipper, and
    anything downstream of it. Those tokens authorize reading and writing the document.

    Compose passes environment variables straight through to the app, so this is one `.env` line away
    from happening by accident. **Do not raise that log level in production.**

If you must debug WOPI, do it on a throwaway instance with throwaway documents, and treat the resulting
logs as secret material. The tokens expire after 30 minutes, which limits the window but does not close
it.

`Logging__LogLevel__Default=Debug` is a different, safer knob — it raises easydocs' own application
logging without turning on framework request logging. Prefer it.

## Upgrading

```bash
cd easydocs/deploy/compose
git pull
# Take a backup FIRST — the new container migrates your schema on startup.
docker compose up -d --build
```

Watch the logs on first start. Migrations run before the app serves traffic; if one fails, the app does
not start, and the log line tells you which.

## Health and monitoring

```bash
curl -fsS http://localhost:8080/health
# {"status":"ok"}
```

`/health` is unauthenticated and unmetered, suitable for a load-balancer probe. It confirms the process
is serving; it does not probe Postgres, Collabora, or the blob volume. For a fuller check, request a
document listing with an `ed_` token — that exercises the database — and download a version, which
exercises the blob store.

Things worth alerting on that easydocs does not alert on for you:

- **Free space on the blob volume.** Versions are retained forever by design; there is no garbage
  collection. Disk use only grows.
- **PDF rendering failures.** LibreOffice runs as a child process with a timeout and retry; a publish
  whose PDF never appears shows up in the application log, not in the UI.
- **Collabora reachability.** If Collabora is down, editing fails while the rest of the app is fine, so
  `/health` stays green.

## Not in v1

- **No S3 blob backend.** Blobs are on the filesystem volume. An S3 backend is a planned
  env-configurable swap for **v1.1**.
- **No OIDC/SSO** and **no MFA.** Local email/password (Argon2id) or `ed_` API tokens only. Both are
  **v1.1** at the earliest. If you need either now, put an authenticating reverse proxy in front of
  easydocs.
- **No antivirus scanning on upload.** v1 trusts `.docx` files from authenticated organization members.
  If your threat model includes malicious members, scan the blob store out of band.
- **No blob garbage collection.** Nothing is ever deleted from the blob volume.
- ~~No durable job queue.~~ **Since v1.1** diff and PDF jobs are rows in the `BackgroundJobs` table,
  enqueued in the same transaction as the work that needs them: a restart picks queued jobs back up,
  a failing job retries with a two-minute backoff, and a job that fails five times is dropped with
  its payload in the application log.

See `SECURITY.md` in the repository for the full list of known v1 limitations and how to report a
vulnerability.

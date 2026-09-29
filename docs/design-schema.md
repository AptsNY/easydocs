# easydocs — Domain Model & Schema

The companion to [architecture-decisions.md](architecture-decisions.md): the ADRs say *why*, this
says *what*. Every diagram below is generated-from-nothing — it mirrors `src/EasyDocs.Api/Domain/`
and the EF Core model in `Data/EasyDocsDbContext.cs`, which are the source of truth.

**How classes become schema.** There is no separate schema definition: each C# class in `Domain/`
is an EF Core entity and maps 1:1 to a Postgres table (`DocumentVersion` → `Versions`, the rest by
pluralized name). Conventions applied across the model:

- **Enums are stored as strings** (`VersionSource`, `BranchKind`, `OrgRole`, `DocRole`) — readable
  in psql, no magic numbers.
- **Every FK is `ON DELETE RESTRICT`.** Soft-delete (`DeletedAt`) plus immutable versions mean a
  cascade must never fire; the schema refuses deletes that would dangle history.
- **No navigation properties.** Entities are flat records; queries join explicitly. Keeps the model
  legible and the SQL predictable.
- **Secrets never at rest.** `ShareLink`, `Invitation`, `ApiToken` store SHA-256 `TokenHash`es;
  recovery codes on `User` are hashes; the raw value is returned exactly once at creation.
- **Guid PKs default to `gen_random_uuid()`** DB-side; `Email` is `citext` (unique,
  case-insensitive); `DocumentText.SearchVector` is a **stored generated `tsvector`** with a GIN
  index — Postgres computes it, the app never writes it.
- **Migrations run at container boot** (`Database.Migrate()`), so a self-hoster's upgrade is
  "pull the new image".

## The core: organizations, documents, versions

Identity on the left, the versioning engine on the right. `Branch` + `DocumentVersion` form the
DAG: every version belongs to a branch, points at its parent, and (for merges) at a second
merge-parent. `Blob` is content-addressed — `Sha256` **is** the primary key — so versions across
all documents and orgs share identical bytes.

```mermaid
erDiagram
    Organization ||--o{ OrgMember : "has members"
    User ||--o{ OrgMember : "belongs via"
    Organization ||--o{ Folder : contains
    Folder |o--o{ Folder : nests
    Organization ||--o{ Document : owns
    Folder |o--o{ Document : groups
    Document ||--o{ DocumentMember : "grants access via"
    User ||--o{ DocumentMember : "is member via"
    Document ||--o{ Branch : "has"
    Branch ||--o{ DocumentVersion : "sequences"
    DocumentVersion |o--o{ DocumentVersion : "parent / merge-parent"
    Blob ||--o{ DocumentVersion : "bytes (BlobSha256, PdfBlobSha256)"
    Document |o--o| DocumentText : "search index"
    Document |o--o{ Document : "copy of (ParentDocumentId)"

    Organization {
        guid Id PK
        string Slug UK
        string Name
    }
    User {
        guid Id PK
        citext Email UK
        string DisplayName
        string PasswordHash "null = SSO-only"
        string TotpSecret "null = MFA off"
        string_array RecoveryCodeHashes
    }
    OrgMember {
        guid OrgId PK,FK
        guid UserId PK,FK
        string Role "Owner|Admin|Member"
    }
    Folder {
        guid Id PK
        guid OrgId FK
        guid ParentId FK "nullable"
        string Name
    }
    Document {
        guid Id PK
        guid OrgId FK
        guid FolderId FK "nullable"
        string Name
        guid ParentDocumentId FK "copies"
        guid ForkedFromVersionId FK
        int VersionCounterMajor "authoritative numbering"
        int VersionCounterMinor
        int VersionCounterRev
        datetime DeletedAt "soft delete = trash"
    }
    DocumentMember {
        guid DocumentId PK,FK
        guid UserId PK,FK
        string Role "Owner|Editor|Viewer"
    }
    Branch {
        guid Id PK
        guid DocumentId FK
        int Ordinal "unique per document"
        string Kind "Main|Concurrent|IncomingPush"
        guid RootVersionId FK
        guid MergedIntoVersionId FK "null = unmerged"
    }
    DocumentVersion {
        guid Id PK
        guid DocumentId FK
        guid BranchId FK
        int SeqInBranch "unique per branch"
        guid ParentVersionId FK
        guid MergeParentVersionId FK
        int Major
        int Minor
        int Revision
        string Source "Upload|EditWopi|EditWebdav|Import|Merge|Revert|CopyPush"
        string PublishedKind "null|minor|major"
        string BlobSha256 FK
        string PdfBlobSha256 FK "rendered on publish"
    }
    Blob {
        string Sha256 PK "content address"
        long SizeBytes
        string Mime "re-sniffed at serve time"
    }
    DocumentText {
        guid DocumentId PK,FK
        string Text "extracted from main head"
        tsvector SearchVector "generated, GIN-indexed"
    }
```

## The collaboration surface

Everything that *points at* the core: edit sessions (browser and Word saves), approvals, share
links, invitations, push-back between copies, API tokens, the audit trail, and the job queue.

```mermaid
erDiagram
    DocumentVersion ||--o{ EditSession : "base of"
    DocumentVersion ||--o{ ApprovalRequest : "decided on"
    DocumentVersion ||--o{ ShareLink : "shared as"
    Document ||--o{ PushRequest : "copy pushes back"
    Organization ||--o{ Invitation : invites
    Organization ||--o{ ApiToken : scopes
    Organization ||--o{ AuditEvent : records

    EditSession {
        guid Id PK "WOPI file_id / DAV session"
        guid DocumentId FK
        guid BaseVersionId FK "branch-on-stale anchor"
        guid UserId FK
        string LockValue "shared by WOPI + WebDAV"
        string LastCommittedSha "what the editor saved last"
        datetime ClosedAt
    }
    ApprovalRequest {
        guid Id PK
        guid VersionId FK
        guid ApproverId FK
        string Decision "null|approved|rejected — immutable once set"
        datetime DueAt
        datetime CancelledAt
    }
    ShareLink {
        guid Id PK
        guid VersionId FK "version-scoped, not document"
        string TokenHash UK "raw token shown once"
        datetime ExpiresAt
        datetime RevokedAt
        int ViewCount "every anonymous view audited"
    }
    PushRequest {
        guid Id PK
        guid CopyDocumentId FK
        guid TargetDocumentId FK
        guid SourceVersionId FK
        string Status "pending|accepted|rejected"
        guid MaterializedVersionId FK "the incoming branch, on accept"
    }
    Invitation {
        guid Id PK
        guid OrgId FK
        citext Email
        string Role "org role on accept"
        guid DocumentId FK "optional doc grant"
        string TokenHash UK
    }
    ApiToken {
        guid Id PK
        guid OrgId FK
        guid UserId FK "capability is capped by this user's role"
        string TokenHash UK "ed_ prefix, hashed at rest"
    }
    AuditEvent {
        guid Id PK
        guid OrgId "FK-light: append-only"
        guid DocumentId "nullable"
        guid ActorUserId "nullable — anonymous share views"
        string Action "version.created, share_link.viewed, ..."
        jsonb Metadata
    }
    BackgroundJob {
        long Id PK "claim order"
        string Type "diff|pdf|extract"
        string Payload "JSON"
        int Attempts "dropped loudly at 5"
        datetime RunAfter "lease + retry backoff"
    }
```

## Components: who talks to whom

```mermaid
flowchart LR
    subgraph Clients
        SPA["React SPA<br/>(served from wwwroot)"]
        Word["Desktop Word<br/>(ms-word: + WebDAV)"]
        Scripts["Scripts / integrations<br/>(ed_ tokens)"]
        Anon["Share-link visitor<br/>(no account)"]
    end

    subgraph App["One container: ASP.NET Core"]
        API["REST API /api/v1<br/>+ OpenAPI at /docs"]
        WOPI["WOPI host /wopi"]
        DAV["WebDAV /dav/{token}"]
        Share["/s/{token} public viewer"]
        SSE["SSE /documents/{id}/events"]
        CS["VersioningService<br/>CommitSaveAsync — the single write path"]
        subgraph Workers["Background workers (queue = Postgres rows)"]
            DW["diff summary"]
            PW["PDF render → LibreOffice"]
            TW["text extract → search index"]
            GC["blob GC (daily sweep)"]
        end
    end

    Collabora["Collabora Online<br/>(container 2)"]
    PG[("PostgreSQL<br/>(container 3)<br/>all state incl. job queue")]
    BS[("Blob store<br/>filesystem volume or S3<br/>keyed by sha256")]

    SPA --> API
    SPA -.live updates.-> SSE
    Scripts --> API
    Anon --> Share
    SPA -->|iframe| Collabora
    Collabora -->|GetFile / PutFile| WOPI
    Word -->|PROPFIND / GET / LOCK / PUT| DAV

    API --> CS
    WOPI --> CS
    DAV --> CS
    CS -->|version + audit + jobs,<br/>one transaction| PG
    CS --> BS
    Workers --> PG
    Workers --> BS
```

## The write path: what one save actually does

Any editor, same sequence. The job rows commit **in the same transaction** as the version — a job
exists iff the work it serves committed (ADR-4).

```mermaid
sequenceDiagram
    participant E as Editor (Collabora PutFile / Word PUT / upload)
    participant CS as CommitSaveAsync
    participant BS as Blob store
    participant PG as Postgres
    participant W as Workers
    participant UI as Every open browser (SSE)

    E->>BS: stream bytes → sha256 (dedup: identical content = no-op)
    E->>CS: commit(document, sha, base version)
    CS->>PG: lock document row (FOR UPDATE)
    alt base is the branch head
        CS->>PG: new version, fast-forward (0.0.Z+1)
    else base is stale (someone saved first)
        CS->>PG: new Concurrent branch + version on it
    end
    CS->>PG: audit row + diff job + extract job (same txn)
    CS-->>E: version id + number
    CS-)UI: version.created
    W->>PG: claim jobs (FOR UPDATE SKIP LOCKED)
    W->>BS: compute diff summary / extract text
    W-)UI: diff.ready
```

## What a history looks like

The `Branch`/`DocumentVersion` rows draw this DAG — it's what the History tab's Graph toggle
renders. Concurrent branch: two saves from `0.0.2`; the merge lands the branch's work on main as
tracked changes and stamps `MergedIntoVersionId`.

```mermaid
gitGraph
    commit id: "0.0.1 upload"
    commit id: "0.0.2 edit_wopi"
    branch concurrent-1
    checkout main
    commit id: "0.0.3 edit_wopi (Ana)"
    checkout concurrent-1
    commit id: "0.0.2+1 edit_webdav (Ben)"
    checkout main
    merge concurrent-1 id: "0.0.4 merge (Ben's changes tracked)"
    commit id: "1.0.0 publish major" type: HIGHLIGHT
```

## Where to read the real thing

| Layer | Where |
|---|---|
| Entities ("classes") | `src/EasyDocs.Api/Domain/*.cs` — one file per table, no navigations |
| Mapping & constraints | `src/EasyDocs.Api/Data/EasyDocsDbContext.cs` |
| Actual DDL | `src/EasyDocs.Api/Data/Migrations/` (generated, never hand-edited) |
| The single write path | `src/EasyDocs.Api/Versioning/VersioningService.cs` |
| Endpoint groups | `src/EasyDocs.Api/{Auth,Documents,Editing,Publishing,Approvals,Copies,Sharing,Diffing}/` |
| Why it's shaped this way | [architecture-decisions.md](architecture-decisions.md) and `docs/superpowers/specs/` |

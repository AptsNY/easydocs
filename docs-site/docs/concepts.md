# Concepts

The mental model. Most of what surprises new users is in this page, and none of it is hard — but the
version-numbering rule in particular is not what people guess.

## Versions are immutable

Every save produces a new **version**: a numbered, attributed, timestamped row pointing at an immutable
blob. Nothing is ever edited in place and nothing is ever silently overwritten. Reverting does not
delete anything; it appends a new version whose content equals an older one.

Blobs are **content-addressed by sha256**, written once and referenced many times. That is why revert,
copies, and push-back are cheap pointer operations rather than file copies — and why re-saving an
unchanged document creates no version at all.

## `X.Y.Z` numbering

### The rules

| | Result |
|---|---|
| **Upload / import / edit-save** (a draft) | bumps `Z` — `0.0.1` → `0.0.2` → `0.0.3` |
| **Publish minor** | `X.(Y+1).0` — `0.0.7` → `0.1.0` |
| **Publish major** | `(X+1).0.0` — `0.0.7` → `1.0.0` |
| **Manual override** | any three non-negative integers, including `0.0.0` |

A first upload is always **`0.0.1`**. Drafts stay in the revision digit until somebody publishes.

### The counter lives on the document, not on the branch head

This is the part worth internalising. **The authoritative counter is three columns on the document row**
— `version_counter_major`, `version_counter_minor`, `version_counter_rev` — **not the number of whatever
version currently sits at the head of a branch.**

Two consequences fall straight out of that, and neither is possible with head-based numbering:

- **Manual override works.** Set the counter to `2.4.0` and every subsequent draft continues from
  there, regardless of what numbers already exist in the history.
- **You can publish a version that is not the head.** Publishing renumbers *the version you selected*
  from the counter, and future drafts continue from the new counter. You do not have to make something
  the newest version in order to release it.

Each draft save reads-and-increments the revision counter while holding a per-document row lock, so
concurrent saves serialize and every version gets a distinct `Z`. No two versions ever collide on a
number, even when two people save at the same instant.

### Download filenames

Downloads are named `{org_slug}__{Document_Name}-v{X}.{Y}.{Z}.docx`. Spaces in the document name become
underscores and filesystem-hostile characters are stripped, so the file is safe to save anywhere.

## Branches and merge

### Concurrent edits diverge, they do not overwrite

When you start editing, your session is pinned to the version you opened — its **base version**. When
you save, easydocs compares that base against the current head of the main line:

- **Base is still the head** → your version lands on the main line. This is the normal case.
- **Base is stale**, because someone else saved while you were editing → your version lands on a **new
  concurrent branch** instead.

Nobody's work is discarded and nobody gets an error. The second author's save simply becomes a branch,
shown in the console indented under the main line with a **Merge** button.

### What merge actually does

Merging uses a **merge-into-main** model. The current main-line head — which already carries the first
author's edits — is the base. The incoming branch's changes are rendered on top of it as **Word tracked
changes, attributed to the incoming author**, and committed as a new version with two parent pointers.
The merged branch closes.

You then accept or reject those tracked changes in the editor, exactly as you would any redline. That
is the conflict resolver; there is no automatic three-way resolution of overlapping edits, and there is
deliberately no attempt to hide the conflict from you.

!!! note "Why the incoming author, and not both authors"
    The obvious design — compare the common ancestor against both sides and consolidate two authors'
    revisions — is not implementable with the open-source comparison engine. `WmlComparer.Compare`
    flattens any pre-existing revisions and stamps exactly **one** author per call, so chaining two
    comparisons makes the first author's edits appear as the second author's *deletions*, which is
    actively misleading. Merge-into-main is the honest alternative. Both branch versions persist in
    history regardless, so nothing is lost either way.

If the comparison engine fails on a document — complex tables, content controls, awkward numbering —
the merge is refused cleanly with "merge unavailable". It never half-commits.

## Redline diff without Track Changes

The signature feature: compare **any two versions** of a document and get a real redline, computed from
the documents themselves. Nobody has to have remembered to turn Track Changes on.

Three shapes of comparison:

- **Numeric summary** — the counts shown on each version row. Computed eagerly by a background worker
  right after each save, so the list reads "14 insertions, 3 deletions" without you opening anything.
- **HTML render** — the redline in the browser. Computed on first request and cached permanently.
- **Redline `.docx`** — the same comparison as a Word document with tracked changes, for downloading.

### Moves and formatting-only changes are always reported as zero

Be clear about this before you rely on the numbers. The comparison engine **classifies insertions and
deletions only**. So:

- A block of text **moved** from one place to another is reported as **one deletion plus one
  insertion**, never as a move.
- An edit that changes **only formatting** — bold, a font, a style, indentation — is **not counted at
  all**.

The `moves` and `formatChanges` fields exist in the API response and are **hard-coded to `0`**. They are
not displayed anywhere in the UI, precisely so that a zero is never presented to you as "no moves
occurred". Treat them as unimplemented, not as measurements.

Insertion and deletion counts are real. The rendered redline is real, and it shows moved text as it
actually is — removed there, added here.

## Publish, PDF, and approvals

### Publishing

Publishing marks a chosen version as a release, as **minor** or **major**, optionally with a publish
name. Three things happen: the version is renumbered from the document counter, a **PDF is rendered**
by a background worker (LibreOffice, out of process, with a timeout and retry), and the version appears
on the **Major Versions** tab.

Downloading a version as PDF before it is published returns an error — no PDF exists yet, and easydocs
would rather say so than render one on the fly and imply it is a release artifact.

### Approvals

Approvals are deliberately minimal — a sign-off, not a discussion.

- **Only on published versions.** Requesting approval on a draft is rejected.
- **One row per approver.** Ask three people and you get three independent approval rows, each with its
  own optional due date.
- **Every approver must already be a member of the document.** This is enforced, and it matters: the
  respond endpoint authorizes on being the named approver, so without the membership check an approver
  would hold a decision right over a version they cannot read.
- **A single immutable decision plus a comment.** Approve or reject, once, with one comment. Decisions
  cannot be edited afterwards.
- **No threaded conversation.** There are no replies, no comment threads, no task system. If you need a
  conversation, have it somewhere else and record the outcome here.
- **Cancellable while open.** The requester can cancel an undecided request, which closes it.

## Copies and push-back

A **copy** is how you get work in front of someone who must not see the rest of your drafting.

Forking a version into a copy creates a **separate document** with:

- **its own members** — copies do not inherit membership from the original, so an outside reviewer
  invited to the copy sees only the copy;
- **its own version history**, starting from the forked version;
- **the same underlying blob**, referenced not duplicated.

Internal drafts never leak into a copy, because there is nothing linking a copy's members to the
original's history.

### Pushing back

When the copy has work worth returning, any member of the copy can **push** a version back to the
original. Authorization is on membership of the **copy**, not the original — this is the one sanctioned
exception to easydocs' per-document authorization rule, and it is what makes the whole external-review
workflow possible.

- If the pusher **also** holds a role on the target document, the push is materialized immediately.
- Otherwise it becomes a **pending push request**, and a member of the target **accepts or rejects** it.
  Accepted, it lands as a clearly-labelled **incoming branch** you can merge like any other. Rejected,
  it never enters the history at all and the pusher is notified.

Cross-document merge works because the fork point travels with the incoming branch, so the common
ancestor can be resolved without walking into the copy's history.

## Sharing

A **share link** hands one version to someone with no account at all.

- **Scoped to exactly one version.** Not the document, not "the latest" — the specific version you
  shared. Later versions are not reachable through it.
- **Revocable**, and optionally **expiring** at a date you set.
- **Audited.** Every anonymous view writes a row to the document's audit trail, including a view
  counter. This is the one read that easydocs audits.
- **Anonymous to the recipient.** They see a plain landing page with the document name, the version
  number, and a download button. No app chrome, no sign-up wall, no account required.

Only a **hash** of the token is stored. The raw token is returned once, when you create the link, and is
unrecoverable afterwards — so the list of your share links shows you when each was created, by whom,
its view count, and whether it is revoked or expired, but never the link itself. Lost the URL? Create a
new link and revoke the old one.

Either the link's creator or an Editor of the document may revoke it.

## Organizations and membership

An **organization** is the outer boundary; every query is filtered by it. Registering creates one and
makes you its owner.

**Membership is strictly per-document.** An organization role — owner, admin — governs organization and
member management. It grants **no implicit access to any document**. Being an org owner does not let you
read a document you were not added to. This is enforced at a single authorization chokepoint with no
role fallback, and it is what makes copy isolation trustworthy.

**A session carries exactly one organization**, chosen as your oldest membership. Accepting an
invitation rebinds your session to the inviting organization. There is no org switcher in v1 — this is
a navigation limit, not an access-control one.

## Audit trail

Every mutation is recorded append-only, per document, along with public share-link reads. Ordinary
authenticated reads are **not** audited — that would be write amplification for very little value. If
you need "who viewed this document", v1 does not answer it; "who changed this document, and who opened
the link I sent out" it answers completely.

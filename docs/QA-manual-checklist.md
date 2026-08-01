# easydocs — hands-on QA checklist

A human pass over the whole product, in a browser. Automation already covers 327 API tests and 84
browser tests; this document is for the things a machine cannot judge — does the redline *read*
correctly, does the PDF *look* right, does an error message actually help — plus a full lifecycle walk
to catch anything the suites agree with each other about but get wrong together.

Work top to bottom. Later sections reuse what earlier ones create.

**Tick the box only when you saw the expected result.** If something differs, write what you saw in the
margin and keep going — one failure rarely blocks the rest.

---

## Before you start

- [ ] **0.1** Stack is up: `cd deploy/compose && docker compose up --build`, then open
      <http://localhost:8080>. All three containers (app, postgres, collabora) report healthy.
- [ ] **0.2** Have two `.docx` files ready that differ by a few visible sentences — real documents from
      your own work are better here than synthetic ones. Call them **A.docx** and **B.docx**. The repo's
      `web/e2e/fixtures/base.docx` and `edited.docx` work if you'd rather not find your own.
- [ ] **0.3** Open a second browser **profile** (not just a private window — you need two independent
      cookie jars that both persist). You'll be two people for most of this.

> Naming used below: **Alice** is the first account (owner of the org), **Bob** is the second.

---

## 1 · Accounts, organizations, invitations

- [ ] **1.1** As Alice, register at `/login` → "Create a new organization". Email, name, password (12+
      chars), org name.
      **Expect:** you land on an empty dashboard; the header shows your org name and your display name.
- [ ] **1.2** Reload the page.
      **Expect:** still signed in. (The session is an httpOnly cookie; a reload must not sign you out.)
- [ ] **1.3** Try registering again with Alice's email in the second profile.
      **Expect:** a clear "A user with that email already exists." — not a stack trace, not a blank form.
- [ ] **1.4** In the second profile, register **Bob** with his own org name.
      **Expect:** Bob sees his own empty dashboard and his own org in the header. He must NOT see any of
      Alice's documents.
- [ ] **1.5** As Alice: **Settings → Organization → Invite**, enter Bob's email, pick a role, submit.
      **Expect:** an invitation link and the raw token appear, with a warning that it is shown once.
      Copy the **link**.
- [ ] **1.6** Reload Alice's settings page.
      **Expect:** the token is gone and cannot be recovered. (Only its hash is stored.)
- [ ] **1.7** As **Bob**, paste the invitation link into his browser.
      **Expect:** "Accepting your invitation…", then Bob lands in the app with **Alice's org** named in
      the header.
- [ ] **1.8** As Bob, open the link a second time.
      **Expect:** a readable "already accepted" style message, not a crash. Bob stays signed in.
- [ ] **1.9** As Alice, invite a third address, then try to accept that link **while signed in as Bob**.
      **Expect:** refused — an invitation only works for the person it was addressed to.
- [ ] **1.10** As Bob: sign out, sign back in.
      **Expect:** Bob can reach Alice's org again. Because Bob's own org is his oldest membership he may
      land there first — if so, an **organization dropdown** is present in the header; switching to
      Alice's org reloads the app into it. *(If there is no dropdown and no way back into Alice's org,
      that is a bug — record it.)*
- [ ] **1.11** As Bob, sign out, then press the browser **Back** button.
      **Expect:** you are NOT back inside the app. Sign-out must clear the session cookie server-side,
      not just hide the UI.

---

## 2 · Folders (E1)

As Alice.

- [ ] **2.1** Create a folder, then a folder inside it, then a third inside that — three levels deep.
      **Expect:** the tree shows the nesting and the current path.
- [ ] **2.2** Create a second folder with the **same name** as an existing sibling.
      **Expect:** refused with the API's own words, not a silent no-op or a duplicate row.
- [ ] **2.3** Rename a folder.
      **Expect:** the new name appears in the tree and in the path immediately.
- [ ] **2.4** Delete a folder that has children.
      **Expect:** you are asked to choose — **promote** the children up a level, or send them to trash.
      Try promote. Confirm the children survived at the parent level.
- [ ] **2.5** Try to drag/move a folder into its own descendant (if the UI offers a move control).
      **Expect:** refused. A folder cannot become its own ancestor.

---

## 3 · Upload and version numbering (E2, E5)

- [ ] **3.1** Create a document, upload **A.docx** as its first version.
      **Expect:** the version number is exactly **0.0.1** — not 0.0.0, not 1.0.0, not blank.
- [ ] **3.2** Look at a document with no versions yet on the dashboard.
      **Expect:** it says so in words. Never "null", never "0.0.0".
- [ ] **3.3** Upload **B.docx** as a second version to the same document.
      **Expect:** **0.0.2**, listed above 0.0.1, with your name, the time, and a change summary
      (insertions/deletions). The summary must not read 0/0/0/0 for a genuinely changed file.
- [ ] **3.4** Look at the very first version's change summary.
      **Expect:** a dash or similar. A version with no parent has nothing to compare against — it must
      not claim "0 insertions".
- [ ] **3.5** Download a version. Check the **filename**.
      **Expect:** it carries the document name and the version number, and exactly one extension — not
      `Lease.docx-v0.0.1.docx`.
- [ ] **3.6** Upload a **PDF** as a version to a new document, then download it.
      **Expect:** it downloads as a `.pdf` that actually opens in a PDF reader. Non-`.docx` files are
      second-class (no editing, no diffing) but must be labelled honestly.

---

## 4 · Editing in Collabora (E3)

- [ ] **4.1** Actions → **Open in Collabora** on the latest version.
      **Expect:** the real Collabora editor loads with your document's content — not a blank frame, not
      an error page. Give it a few seconds on first load.
- [ ] **4.2** Type a visible sentence, then close the editor / return to the document.
      **Expect:** a new version appears with your edit, attributed to you.
- [ ] **4.3** Open the editor again and close it **without changing anything**.
      **Expect:** **no new version.** An unchanged re-save must not create version spam.
- [ ] **4.4** Open the edited version in Collabora and confirm your sentence from 4.2 is present.
      **Expect:** the bytes round-tripped intact.

---

## 5 · Concurrent editing — branch and merge (E4)

This is the headline feature. Take your time here.

- [ ] **5.1** Make Bob a member of the document (document **Members** panel → add Bob → Editor).
- [ ] **5.2** Alice and Bob **both** open the *same* version in Collabora, at the same time, in their two
      browser profiles.
- [ ] **5.3** Each types a *different*, clearly identifiable sentence. Both close the editor.
- [ ] **5.4** Look at the document console history.
      **Expect:** two versions branched from the same parent — shown as an indented group, with a
      **Merge** button. **Neither edit is lost.**
- [ ] **5.5** Press **Merge**.
      **Expect:** a new version is created. Open it: **both** sentences are present, and the incoming
      author's changes are attributed to them as tracked changes.
- [ ] **5.6** Check history again.
      **Expect:** both original branch versions still exist. The merged branch is closed/marked merged.
      Nothing was deleted to make the merge tidy.

---

## 6 · Compare and redline

- [ ] **6.1** Compare 0.0.1 against the latest version.
      **Expect:** a readable redline — insertions and deletions marked. **Read it.** Does it actually
      reflect the changes you made, or is it noise?
- [ ] **6.2** Compare a version **with itself**.
      **Expect:** a message saying so. Not a blank pane.
- [ ] **6.3** Compare against a version that cannot be diffed (e.g. involving the PDF from 3.6).
      **Expect:** a graceful human message, not a broken pane or a spinner forever.
- [ ] **6.4** **Download redline** from the compare view.
      **Expect:** a `.docx` that opens in Word and shows the same changes as tracked changes.

---

## 7 · Publish and PDF (E6)

- [ ] **7.1** Actions → **Publish** → **minor** on a chosen version.
      **Expect:** the version renumbers (e.g. `0.0.7 → 0.1.0`) and appears under **Major Versions** with
      kind, number, publisher name and date.
- [ ] **7.2** Publish another version as **major**.
      **Expect:** renumbers to the next whole number (e.g. `→ 1.0.0`).
- [ ] **7.3** Wait a moment, then look for the PDF on the published version.
      **Expect:** a PDF link appears once rendering finishes. **Open it.** Does it look like the
      document — fonts, tables, page breaks — or is it mangled?
- [ ] **7.4** Confirm publishing applied to the version you *selected*, not simply to the newest one.

---

## 8 · Approvals (E7)

- [ ] **8.1** Try to request approval on an **unpublished** version.
      **Expect:** not offered, or refused. Approvals are for published versions only.
- [ ] **8.2** On a published version, **Request approval** from Bob, with a due date.
      **Expect:** one approval row per approver, showing the due date and an open status.
- [ ] **8.3** As Bob, open **Approvals → Asked of me**.
      **Expect:** the request is listed with the document name and version number — not a bare id.
- [ ] **8.4** As Bob, respond with a decision and a comment.
      **Expect:** recorded, and now shown as decided.
- [ ] **8.5** As Bob, try to change that decision.
      **Expect:** impossible. Decisions are immutable, and there is no comment thread.
- [ ] **8.6** As Alice, request another approval, then **cancel** it while open.
      **Expect:** the request closes.
- [ ] **8.7** Check the **open** filter in the inbox.
      **Expect:** decided and cancelled items are excluded.

---

## 9 · The Actions menu (E8)

- [ ] **9.1** Open **Actions** on a version. Count the entries.
      **Expect:** exactly these eight, all working: Open in Collabora, Import, Share, Download, Name,
      Publish, Revert, Push To Copy.
- [ ] **9.2** Press **Escape**.
      **Expect:** the menu closes and focus returns to the Actions button.
- [ ] **9.3** Walk the whole menu with **Tab** only, no mouse.
      **Expect:** every item is reachable and activates with Enter/Space.
- [ ] **9.4** **Name** a version, then find it in history.
      **Expect:** the name is shown alongside the number.
- [ ] **9.5** **Import** a `.docx` through the menu.
      **Expect:** a new version whose source is recorded as an import.

---

## 10 · Copies and push-back review (E9)

- [ ] **10.1** Actions → **Push To Copy**, name the copy.
      **Expect:** a new, separate document with its own history.
- [ ] **10.2** Open the copy. Check its **Members**.
      **Expect:** isolated — the original's members are NOT carried over.
- [ ] **10.3** Add Bob to the **copy** only. As Bob, confirm he can open the copy.
      **Expect:** Bob can see the copy and **cannot** see the original document. Copies must never leak
      the master's drafts.
- [ ] **10.4** As Bob, edit the copy and push the work back to the original.
      **Expect:** it does not land directly. It becomes a pending review request.
- [ ] **10.5** As Alice, find the pending push and **reject** it.
      **Expect:** it never enters the original's history.
- [ ] **10.6** Repeat and **accept** one.
      **Expect:** it lands as a clearly-labelled incoming branch, attributed to Bob.
- [ ] **10.7** Open the **Copies** tab on the original.
      **Expect:** the copies made from this document are listed.

---

## 11 · Share links (E10)

- [ ] **11.1** Actions → **Share**, create a link with no expiry. Copy it.
- [ ] **11.2** Open the link in a **private window with no session at all**.
      **Expect:** a plain download page showing the document name and version number. **No app chrome,
      no sign-in wall, no sign-up prompt.**
- [ ] **11.3** Download the file from that page.
      **Expect:** it works, with no account.
- [ ] **11.4** Back as Alice, open the Share dialog again.
      **Expect:** the link is listed, with a view count that went up, and a **Revoke** button.
- [ ] **11.5** **Revoke** it, then reload the public link.
      **Expect:** a human "this link is no longer available" message.
- [ ] **11.6** Visit `/s/some-token-that-never-existed`.
      **Expect:** *the same* message as the revoked one — a probe must not learn which tokens are real.
- [ ] **11.7** Create a link with an expiry a minute out. Wait for it to pass, then open it.
      **Expect:** the same dead-link message.
- [ ] **11.8** Open the document's **Audit** tab.
      **Expect:** the anonymous view is recorded, attributed as anonymous rather than to a user.

---

## 12 · Revert (E11)

- [ ] **12.1** Actions → **Revert** on an older version.
      **Expect:** a **new** version is created at the head whose content equals the target's.
- [ ] **12.2** Check the history.
      **Expect:** nothing was rewritten or deleted. Reverting adds; it never edits the past.

---

## 13 · Roles and access (E12)

- [ ] **13.1** Set Bob to **Viewer** on a document. As Bob, open **Actions**.
      **Expect:** only the read-only actions (Share, Download). No Publish, no Revert, no editing.
- [ ] **13.2** As Bob (Viewer), open the Members panel.
      **Expect:** he can read the roster but gets no controls to change it.
- [ ] **13.3** Try to remove or demote the **last Owner** of a document.
      **Expect:** refused, with the API's reason shown.
- [ ] **13.4** As Bob, paste the URL of a document he is **not a member of** directly into the address
      bar.
      **Expect:** not found — and note that an org role alone must not grant document access.
- [ ] **13.5** Sign in as a user in a **different org** and paste a document URL from Alice's org.
      **Expect:** **404**, not 403. A 403 would confirm the document exists.
- [ ] **13.6** **Settings → API tokens**: create a token. Note it is shown once. Revoke it.
      **Expect:** shown exactly once; revocation available.
- [ ] **13.7** As Bob, look at the API tokens screen.
      **Expect:** Bob sees only **his own** tokens, never Alice's.
- [ ] **13.8** Use a token against the API:
      `curl -H "Authorization: Bearer ed_…" http://localhost:8080/api/v1/documents`
      **Expect:** it works, and returns only what that user may see.
- [ ] **13.9** Revoke the token, run the same command again.
      **Expect:** rejected.

---

## 14 · Organization settings

- [ ] **14.1** Rename the organization.
      **Expect:** the name changes everywhere; the **slug does not**.
- [ ] **14.2** As Alice (Owner), change Bob's org role.
      **Expect:** allowed, and reflected in the member list.
- [ ] **14.3** Try to demote or remove the **last Owner** of the org.
      **Expect:** refused.
- [ ] **14.4** As a plain member, open Settings.
      **Expect:** the roster is readable, but the management controls are absent.

---

## 15 · Errors, resilience, and the rough edges

- [ ] **15.1** Stop the app container, leave the browser open, click around.
      **Expect:** honest error messages. No infinite spinners, no blank white screens.
- [ ] **15.2** Restart the container and reload.
      **Expect:** everything is still there. Nothing depended on process memory.
- [ ] **15.3** Upload a file that is not a document at all (a `.png` renamed to `.docx`).
      **Expect:** either a clear rejection or an honest non-editable version. Never a 500.
- [ ] **15.4** Fail login ~10 times quickly.
      **Expect:** eventually a **429** with a `Retry-After`, and a readable message. Confirm a legitimate
      login still succeeds afterwards once the window passes.
- [ ] **15.5** Leave the document console open in one window while creating a version from another.
      **Expect:** the console updates **without a reload** (SSE).
- [ ] **15.6** Resize to a narrow/mobile width on the dashboard and the console.
      **Expect:** usable, no horizontal scrolling of the whole page, nothing clipped off-screen.
- [ ] **15.7** Tab through the dashboard from the top.
      **Expect:** a "Skip to content" link appears first, focus outlines are visible throughout.

---

## 16 · Judgement calls only a human can make

Automation asserts these exist. You are judging whether they are *good*.

- [ ] **16.1** Is the redline **trustworthy**? Would you send it to a counterparty?
- [ ] **16.2** Does the published **PDF** match the document's formatting closely enough to circulate?
- [ ] **16.3** After a merge, does the result read like a sensible document, or like two documents stapled
      together?
- [ ] **16.4** Do the error messages tell you **what to do next**, or only that something failed?
- [ ] **16.5** Could a colleague who has never seen easydocs upload a document, edit it, and share it
      without being told how?

---

## Known v1 limitations — do not file these as bugs

- No OIDC/SSO, no MFA. Local email + password only.
- No desktop "Open in Word" — editing is in the browser via Collabora.
- No graphical revision graph (history is an indented list).
- No full-text content search — names only.
- No antivirus scanning on upload.
- No cloud export/import pickers.
- Rate limiting is per client IP, so behind a reverse proxy it collapses into one install-wide budget
  unless `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` is set.
- Over plain HTTP on anything other than `localhost`, login will appear to succeed and then silently
  fail — the session cookie is `Secure`. Terminate TLS at a proxy.
- Signing out clears the session cookie, but a JWT already copied out of the browser stays valid until
  it expires.

---

## Result

| Section | Pass | Fail | Notes |
|---|---|---|---|
| 1 Accounts & invitations | | | |
| 2 Folders | | | |
| 3 Upload & numbering | | | |
| 4 Collabora editing | | | |
| 5 Branch & merge | | | |
| 6 Compare & redline | | | |
| 7 Publish & PDF | | | |
| 8 Approvals | | | |
| 9 Actions menu | | | |
| 10 Copies & push | | | |
| 11 Share links | | | |
| 12 Revert | | | |
| 13 Roles & access | | | |
| 14 Org settings | | | |
| 15 Errors & resilience | | | |
| 16 Judgement calls | | | |

**Ship / don't ship:** ______________

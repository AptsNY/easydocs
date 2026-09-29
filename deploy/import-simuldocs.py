#!/usr/bin/env python3
"""Import a Simuldocs export into easydocs: every revision, in order, with its label.

Usage:  ED_TOKEN=ed_xxx [ED_BASE=https://docs.example.com] \\
          python3 import-simuldocs.py /path/to/simuldocs-export [--dry-run]

INPUT. Simuldocs itself ships no bulk export. This reads the layout written by the exporter used to
migrate AptsNY off Simuldocs; build the same shape from any other export and it works too:

  simuldocs-export/            <- pass this directory
    manifest.json              <- a JSON array, one row per revision:
      {"documentId": "…",      stable id grouping a document's revisions
       "document":   "Lease",  document name
       "collection": "Leases", folder name ("" or absent = no folder)
       "order":      3,        revision order within the document (an integer, or integer string)
       "label":      "Signed", becomes the easydocs version name (optional)
       "saved":      "C:\\\\…\\\\simuldocs-export\\\\Leases\\\\Lease\\\\3.docx",
                               where the exporter wrote the file; the part after
                               "simuldocs-export\\\\" is resolved inside this directory
       "result":     "ok"}     the exporter's outcome for this revision
    <collection>/<document>/…  the files `saved` points at

WHAT IS AND IS NOT KEPT. Order and labels are kept. A revision byte-identical to the one before it
adds no version (easydocs dedupes it) and its label is reported, not applied. Timestamps and authors
are not kept: every version is created now, by the token's owner, and no API can backdate them. Run
it as the person who should own the documents: each is created with the token's owner as sole Owner.

ALL OR NOTHING PER DOCUMENT. A document with any revision whose export row is not "ok", or whose file
is missing from this copy, is not imported at all — a history with a hole in it cannot be filled in
later — and makes the run exit non-zero. Fix the export and re-run.

RE-RUNS. Progress is recorded in import-state.json next to the manifest: the easydocs document this
script created for each Simuldocs document, and which revisions (by manifest order) it uploaded.
Documents are created together with their first revision (POST /documents:import), so there is never
an empty document to adopt. A re-run skips finished documents and resumes interrupted ones from the
next revision. It never writes into a document it did not create: a document already present with
the same name in the same folder is reported and left alone, and the run exits non-zero — including
one left by an interrupted run whose create response was lost, which you delete before re-running.
Two export documents with the same name get the first free " (2)", " (3)"... A failed document is
reported and the run carries on; the exit status is non-zero if anything was not imported. A lock
file stops two runs at once.
"""
import json, os, sys, uuid, mimetypes, urllib.request, urllib.error
from urllib.parse import urlparse

BASE = os.environ.get("ED_BASE", "http://localhost:8080").rstrip("/")
TOKEN = os.environ.get("ED_TOKEN", "")

def api(method, path, body=None, ctype="application/json"):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    if body is not None:
        req.add_header("Content-Type", ctype)
        req.data = body if isinstance(body, bytes) else json.dumps(body).encode()
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read() or b"{}")

def multipart(path, filepath, fields=None):
    boundary = uuid.uuid4().hex
    fname = os.path.basename(filepath)
    mime = mimetypes.guess_type(fname)[0] or "application/octet-stream"
    with open(filepath, "rb") as f:
        data = f.read()
    parts = b"".join(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode()
                     for k, v in (fields or {}).items() if v is not None)
    body = parts + (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                    f"filename=\"{fname}\"\r\nContent-Type: {mime}\r\n\r\n").encode() \
           + data + f"\r\n--{boundary}--\r\n".encode()
    return api("POST", path, body, f"multipart/form-data; boundary={boundary}")

def local_path(export_root, saved):
    # The tail after 'simuldocs-export\' mirrors this directory's layout. Resolved and then required to
    # stay inside it: a manifest is input, and a '../' or absolute path must not upload a local file.
    tail = saved.replace("\\", "/").split("simuldocs-export/", 1)[-1].lstrip("/")
    root = os.path.realpath(export_root)
    p = os.path.realpath(os.path.join(root, tail))
    try:
        return p if os.path.commonpath([p, root]) == root else None
    except ValueError:  # e.g. another drive on Windows
        return None

def plan(export_root):
    """Group the manifest into documents; name them; mark the incomplete ones."""
    with open(os.path.join(export_root, "manifest.json"), encoding="utf-8-sig") as f:
        manifest = json.load(f)
    docs = {}  # documentId -> {name, collection, revisions[], problems[]}
    for r in manifest:
        d = docs.setdefault(r["documentId"], {
            "name": r["document"].strip(), "collection": (r.get("collection") or "").strip(),
            "revisions": [], "problems": []})
        if r.get("result") != "ok":
            d["problems"].append(f"revision {r.get('order')} was not exported ({r.get('result')})")
        elif not ((p := local_path(export_root, r.get("saved", ""))) and os.path.isfile(p)):
            d["problems"].append(f"revision {r.get('order')}'s file is missing")
        else:
            d["revisions"].append(r)
    for did, d in docs.items():
        try:
            d["revisions"].sort(key=lambda r: int(r["order"]))
        except (KeyError, TypeError, ValueError):
            sys.exit(f"manifest: document {did} ({d['name']}) has a revision whose order is not an integer.")

    # Same name in the same collection would be one easydocs document: give each duplicate the first
    # free suffix. Over the whole manifest, in documentId order, so a name never shifts between runs.
    taken = {(d["collection"], d["name"]) for d in docs.values()}
    seen = set()
    for did in sorted(docs):
        d, key = docs[did], (docs[did]["collection"], docs[did]["name"])
        if key in seen:
            n = 2
            while (d["collection"], f"{d['name']} ({n})") in taken:
                n += 1
            print(f"  RENAME duplicate name: {d['name']} -> {d['name']} ({n})")
            d["name"] = f"{d['name']} ({n})"
            taken.add((d["collection"], d["name"]))
        seen.add((d["collection"], d["name"]))
    return docs

def main():
    args = [a for a in sys.argv[1:] if a != "--dry-run"]
    dry = "--dry-run" in sys.argv
    if len(args) != 1:
        sys.exit(__doc__)
    export_root = args[0]
    if not dry and not TOKEN:
        sys.exit("ED_TOKEN is empty: mint a token in Settings -> API tokens.")
    if urlparse(BASE).scheme == "http" and urlparse(BASE).hostname not in ("localhost", "127.0.0.1"):
        print(f"warning: {BASE} is plain http; the token travels in the clear.", file=sys.stderr)

    docs = plan(export_root)
    incomplete = {k: d for k, d in docs.items() if d["problems"]}
    for d in incomplete.values():
        print(f"  NOT IMPORTED (incomplete): {d['name']}: {'; '.join(d['problems'])}", file=sys.stderr)
    ready = {k: d for k, d in docs.items() if not d["problems"] and d["revisions"]}
    print(f"{len(ready)} documents, {sum(len(d['revisions']) for d in ready.values())} revisions to import;"
          f" {len(incomplete)} incomplete")
    if docs and not ready and not dry:
        sys.exit(f"nothing importable ({len(incomplete)} incomplete). If every file shows as missing, pass the"
                 " simuldocs-export directory itself.")
    if dry:
        sys.exit(1 if incomplete else 0)

    state_path = os.path.join(export_root, "import-state.json")
    lock_path = state_path + ".lock"
    try:
        os.close(os.open(lock_path, os.O_CREAT | os.O_EXCL))
    except FileExistsError:
        sys.exit(f"another import holds {lock_path}; if none is running, delete it and re-run.")
    try:
        failed = run(ready, export_root, state_path) + len(incomplete)
    finally:
        os.remove(lock_path)
    if failed:
        sys.exit(f"{failed} document(s) not imported. Fix the causes above and re-run: finished documents"
                 " are skipped, interrupted ones resume.")

def run(docs, export_root, state_path):
    # Simuldocs documentId -> {"id": easydocs document created by this script,
    #                          "orders": manifest orders uploaded, "last": last version id returned}
    state = {}
    if os.path.exists(state_path):
        with open(state_path, encoding="utf-8") as f:
            state = json.load(f)
    def save_state():
        tmp = state_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(state, f, indent=1)
        os.replace(tmp, state_path)  # atomic: a crash never leaves half a state file

    existing, cursor = set(), None  # (folderId, name) of documents already there
    while True:  # cursor-paginated, max 100/page
        page = api("GET", "/api/v1/documents?limit=100" + (f"&cursor={cursor}" if cursor else ""))
        existing.update((doc.get("folderId"), doc["name"]) for doc in page["items"])
        cursor = page.get("nextCursor")
        if not cursor:
            break
    folders = {f["name"]: f["id"] for f in api("GET", "/api/v1/folders")}

    failed = 0
    for did, d in sorted(docs.items()):
        revs, mine = d["revisions"], state.get(did)
        try:
            def record(r, version_id):
                label = (r.get("label") or "").strip()
                if version_id == mine["last"]:
                    if label:
                        print(f"  note: {d['name']} / '{label}' is identical to the revision before it;"
                              " label not applied")
                elif label:
                    api("PATCH", f"/api/v1/versions/{version_id}", {"name": label})
                mine["orders"].append(int(r["order"]))
                mine["last"] = version_id
                save_state()

            created_now = not mine
            if not mine:
                fid = None
                if d["collection"]:
                    fid = folders.get(d["collection"]) or \
                          api("POST", "/api/v1/folders", {"name": d["collection"], "parentId": None})["id"]
                    folders[d["collection"]] = fid
                if (fid, d["name"]) in existing:
                    failed += 1
                    print(f"NOT IMPORTED: {d['name']}: a document with this name is already in that folder and"
                          " this import did not record creating it. If it is a leftover of an interrupted run,"
                          " delete it; otherwise rename it. Then re-run.", file=sys.stderr)
                    continue
                # Created together with its first revision: there is never an empty document to adopt.
                first = revs[0]
                created = multipart("/api/v1/documents:import", local_path(export_root, first["saved"]),
                                    {"name": d["name"], "folderId": fid})
                mine = state[did] = {"id": created["id"], "orders": [], "last": None}
                record(first, created["versionId"])
            todo = [r for r in revs if int(r["order"]) not in mine["orders"]]
            if not todo:
                print(f"{'imported' if created_now else 'skip (done)'}: {d['name']} ({len(mine['orders'])} revisions)")
                continue
            if int(todo[0]["order"]) < max(mine["orders"]):
                failed += 1
                print(f"FAILED: {d['name']}: revision {todo[0]['order']} is in the export but later revisions were"
                      " already imported, and history cannot be inserted into. To re-import it whole, delete the"
                      f" easydocs document and its \"{did}\" entry in import-state.json.", file=sys.stderr)
                continue
            for r in todo:
                record(r, multipart(f"/api/v1/documents/{mine['id']}/versions:import",
                                    local_path(export_root, r["saved"]))["versionId"])
            print(f"imported: {d['name']} ({len(mine['orders'])} revisions)")
        except urllib.error.HTTPError as e:
            if e.code == 401:
                sys.exit("401 from easydocs: the token is invalid or revoked; nothing more was attempted.")
            failed += 1
            hint = (" (the document may have been trashed or deleted: restore it, or delete its entry in"
                    " import-state.json to import it afresh)") if e.code == 404 else ""
            print(f"FAILED: {d['name']}: HTTP {e.code} {e.read()[:300].decode(errors='replace')}{hint}",
                  file=sys.stderr)
        except (urllib.error.URLError, OSError, ValueError, KeyError) as e:
            failed += 1
            print(f"FAILED: {d['name']}: {type(e).__name__}: {e}", file=sys.stderr)
    return failed

if __name__ == "__main__":
    main()

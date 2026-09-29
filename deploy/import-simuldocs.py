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
       "result":     "ok"}     rows with any other result are counted and skipped
    <collection>/<document>/…  the files `saved` points at

WHAT IS AND IS NOT KEPT. Order and labels are kept. A revision byte-identical to the one before it
adds no version (easydocs dedupes it) and its label is reported, not applied. Timestamps and authors
are not kept: every version is created now, by the token's owner, and no API can backdate them. Run
it as the person who should own the documents: each is created with the token's owner as sole Owner.

RE-RUNS. Progress is recorded in import-state.json next to the manifest: which easydocs document this
script created for each Simuldocs document, and how many of its revisions were uploaded. A re-run
skips finished documents and resumes interrupted ones from exactly where they stopped. It never
writes into a document it did not create: an existing document with the same name in the same folder
is reported and left alone. Two export documents with the same name get " (2)", " (3)"... A failed
document is reported and the run carries on; the exit status is non-zero if anything failed.
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

def upload(doc_id, filepath):
    boundary = uuid.uuid4().hex
    fname = os.path.basename(filepath)
    mime = mimetypes.guess_type(fname)[0] or "application/octet-stream"
    with open(filepath, "rb") as f:
        data = f.read()
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
            f"filename=\"{fname}\"\r\nContent-Type: {mime}\r\n\r\n").encode() \
           + data + f"\r\n--{boundary}--\r\n".encode()
    return api("POST", f"/api/v1/documents/{doc_id}/versions:import", body,
               f"multipart/form-data; boundary={boundary}")

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

    with open(os.path.join(export_root, "manifest.json"), encoding="utf-8-sig") as f:
        manifest = json.load(f)
    rows = [r for r in manifest if r.get("result") == "ok"]
    if len(rows) < len(manifest):
        print(f"  SKIP {len(manifest) - len(rows)} manifest rows whose export result was not ok")

    docs = {}  # documentId -> {name, collection, revisions[]}
    for r in rows:
        d = docs.setdefault(r["documentId"], {
            "name": r["document"].strip(), "collection": (r.get("collection") or "").strip(),
            "revisions": []})
        d["revisions"].append(r)
    for did, d in docs.items():
        try:
            d["revisions"].sort(key=lambda r: int(r["order"]))
        except (KeyError, TypeError, ValueError):
            sys.exit(f"manifest: document {did} ({d['name']}) has a revision whose order is not an integer.")

    # Drop revisions whose file isn't in this copy of the export (warn), then documents left empty.
    for d in docs.values():
        for r in list(d["revisions"]):
            path = local_path(export_root, r.get("saved", ""))
            if path is None or not os.path.isfile(path):
                print(f"  SKIP missing file: {d['name']} / {r.get('label') or r['order']}")
                d["revisions"].remove(r)
    docs = {k: d for k, d in docs.items() if d["revisions"]}

    # Same name in the same collection would be one easydocs document: keep them apart.
    seen = {}
    for did in sorted(docs):
        d = docs[did]
        n = seen[(d["collection"], d["name"])] = seen.get((d["collection"], d["name"]), 0) + 1
        if n > 1:
            print(f"  RENAME duplicate name: {d['name']} -> {d['name']} ({n})")
            d["name"] = f"{d['name']} ({n})"
    total = sum(len(d["revisions"]) for d in docs.values())
    print(f"{len(docs)} documents, {total} revisions in the export")
    if rows and not total:
        sys.exit("no manifest file resolved: pass the simuldocs-export directory itself.")
    if dry:
        return

    state_path = os.path.join(export_root, "import-state.json")
    state = {}  # Simuldocs documentId -> {"id": easydocs document id, "done": revisions uploaded}
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
        if mine and mine["done"] >= len(revs):
            print(f"skip (done): {d['name']}")
            continue
        try:
            if not mine:
                fid = None
                if d["collection"]:
                    fid = folders.get(d["collection"]) or \
                          api("POST", "/api/v1/folders", {"name": d["collection"], "parentId": None})["id"]
                    folders[d["collection"]] = fid
                if (fid, d["name"]) in existing:
                    print(f"skip (a document with this name already exists; not created by this import): {d['name']}")
                    continue
                mine = state[did] = {"id": api("POST", "/api/v1/documents", {"name": d["name"], "folderId": fid})["id"],
                                     "done": 0, "last": None}
                save_state()
            elif mine["done"]:
                print(f"resume: {d['name']} from revision {mine['done'] + 1} of {len(revs)}")
            for r in revs[mine["done"]:]:
                v = upload(mine["id"], local_path(export_root, r["saved"]))
                label = (r.get("label") or "").strip()
                if v["versionId"] == mine.get("last"):
                    if label:
                        print(f"  note: {d['name']} / '{label}' is identical to the revision before it; label not applied")
                elif label:
                    api("PATCH", f"/api/v1/versions/{v['versionId']}", {"name": label})
                mine["done"], mine["last"] = mine["done"] + 1, v["versionId"]
                save_state()
            print(f"imported: {d['name']}")
        except urllib.error.HTTPError as e:
            if e.code == 401:
                sys.exit("401 from easydocs: the token is invalid or revoked; nothing more was attempted.")
            failed += 1
            print(f"FAILED: {d['name']}: HTTP {e.code} {e.read()[:300].decode(errors='replace')}",
                  file=sys.stderr)
        except (urllib.error.URLError, OSError, ValueError, KeyError) as e:
            failed += 1
            print(f"FAILED: {d['name']}: {type(e).__name__}: {e}", file=sys.stderr)

    if failed:
        sys.exit(f"{failed} document(s) failed. Fix the cause and re-run: finished documents are skipped,"
                 " interrupted ones resume where they stopped.")

if __name__ == "__main__":
    main()

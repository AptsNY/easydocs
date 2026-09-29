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
       "order":      3,        revision order within the document (number)
       "label":      "Signed", becomes the easydocs version name (optional)
       "saved":      "C:\\\\…\\\\simuldocs-export\\\\Leases\\\\Lease\\\\3.docx",
                               where the exporter wrote the file; the part after
                               "simuldocs-export\\\\" is resolved inside this directory
       "result":     "ok"}     rows with any other result are counted and skipped
    <collection>/<document>/…  the files `saved` points at

WHAT IS AND IS NOT KEPT. Order and labels are kept. Timestamps and authors are not: every version is
created now, by the token's owner, and no easydocs API can backdate them. Run it as the person who
should own the documents: each one is created with the token's owner as its sole Owner, and a re-run
only sees documents that person can see.

RE-RUNS. A document already present in its folder is skipped when it has all its revisions, and
resumed from the next revision when an earlier run died partway (it compares versionCount). A failed
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
    return p if os.path.commonpath([p, root]) == root else None

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
    for d in docs.values():
        d["revisions"].sort(key=lambda r: int(r["order"]))

    # Drop revisions whose file isn't in this copy of the export (warn), then documents left empty.
    for d in docs.values():
        for r in list(d["revisions"]):
            path = local_path(export_root, r["saved"])
            if path is None or not os.path.isfile(path):
                print(f"  SKIP missing file: {d['name']} / {r.get('label') or r['order']}")
                d["revisions"].remove(r)
    docs = {k: d for k, d in docs.items() if d["revisions"]}
    total = sum(len(d["revisions"]) for d in docs.values())
    print(f"{len(docs)} documents, {total} revisions in the export")
    if rows and not total:
        sys.exit("no manifest file resolved: pass the simuldocs-export directory itself.")
    if dry:
        return

    existing, cursor = {}, None  # (folderId, name) -> (id, versionCount)
    while True:  # cursor-paginated, max 100/page
        page = api("GET", "/api/v1/documents?limit=100" + (f"&cursor={cursor}" if cursor else ""))
        for doc in page["items"]:
            existing[(doc.get("folderId"), doc["name"])] = (doc["id"], doc.get("versionCount", 0))
        cursor = page.get("nextCursor")
        if not cursor:
            break
    folders = {f["name"]: f["id"] for f in api("GET", "/api/v1/folders")}

    failed = 0
    for d in docs.values():
        revs = d["revisions"]
        try:
            fid = None
            if d["collection"]:
                fid = folders.get(d["collection"]) or \
                      api("POST", "/api/v1/folders", {"name": d["collection"], "parentId": None})["id"]
                folders[d["collection"]] = fid
            doc_id, done = existing.get((fid, d["name"]), (None, 0))
            if doc_id and done >= len(revs):
                print(f"skip (complete): {d['name']}")
                continue
            if doc_id:
                # ponytail: resumes by count, trusting the first `done` versions are the first `done`
                # revisions (true for this script's in-order imports); a label lost to a crash between
                # import and PATCH is not re-applied.
                print(f"resume: {d['name']} has {done} of {len(revs)}")
            else:
                doc_id = api("POST", "/api/v1/documents", {"name": d["name"], "folderId": fid})["id"]
            for r in revs[done:]:
                v = upload(doc_id, local_path(export_root, r["saved"]))
                label = (r.get("label") or "").strip()
                if label:
                    api("PATCH", f"/api/v1/versions/{v['versionId']}", {"name": label})
            print(f"imported: {d['name']} ({len(revs)} versions)")
        except urllib.error.HTTPError as e:
            failed += 1
            print(f"FAILED: {d['name']}: HTTP {e.code} {e.read()[:300].decode(errors='replace')}",
                  file=sys.stderr)
        except (urllib.error.URLError, OSError) as e:
            failed += 1
            print(f"FAILED: {d['name']}: {e}", file=sys.stderr)

    if failed:
        sys.exit(f"{failed} document(s) failed; re-run to resume them.")

if __name__ == "__main__":
    main()

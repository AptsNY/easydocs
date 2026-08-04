#!/usr/bin/env python3
"""Import a Simuldocs export into easydocs, history intact.

Usage:  ED_TOKEN=ed_xxx python3 import-simuldocs.py /path/to/export [--dry-run]

Reads manifest.json, creates one easydocs folder per collection and one
document per Simuldocs document, then imports every revision in order via
POST /documents/{id}/versions:import and names each version with its
Simuldocs label. Idempotent-ish: re-running skips documents whose name
already exists in the target folder.
"""
import json, os, sys, uuid, mimetypes, urllib.request, urllib.error

BASE = os.environ.get("ED_BASE", "http://localhost:8080")
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
    # manifest 'saved' is the exporter's Windows path; the tail after
    # 'simuldocs-export\' mirrors this export directory's layout.
    tail = saved.replace("\\", "/").split("simuldocs-export/", 1)[-1]
    return os.path.join(export_root, tail)

def main():
    export_root = sys.argv[1]
    dry = "--dry-run" in sys.argv
    with open(os.path.join(export_root, "manifest.json"), encoding="utf-8-sig") as f:
        rows = [r for r in json.load(f) if r.get("result") == "ok"]

    docs = {}  # documentId -> {name, collection, revisions[]}
    for r in rows:
        d = docs.setdefault(r["documentId"], {
            "name": r["document"].strip(), "collection": (r.get("collection") or "").strip(),
            "revisions": []})
        d["revisions"].append(r)
    for d in docs.values():
        d["revisions"].sort(key=lambda r: r["order"])

    # Drop revisions whose file isn't in this copy of the export (warn), then
    # drop documents left with no revisions at all.
    for d in docs.values():
        for r in [r for r in d["revisions"] if not os.path.isfile(local_path(export_root, r["saved"]))]:
            print(f"  SKIP missing file: {d['name']} / {r.get('label') or r['order']}")
            d["revisions"].remove(r)
    docs = {k: d for k, d in docs.items() if d["revisions"]}
    total = sum(len(d["revisions"]) for d in docs.values())
    print(f"{len(docs)} documents, {total} revisions to import")
    if dry:
        sys.exit(0)

    existing, cursor = {}, None
    while True:  # cursor-paginated, max 100/page
        page = api("GET", "/api/v1/documents?limit=100"
                   + (f"&cursor={cursor}" if cursor else ""))
        for doc in page["items"]:
            existing[(doc.get("folderId"), doc["name"])] = doc["id"]
        cursor = page.get("nextCursor")
        if not cursor:
            break
    folders = {f["name"]: f["id"] for f in api("GET", "/api/v1/folders")}

    for did, d in docs.items():
        fid = None
        if d["collection"]:
            fid = folders.get(d["collection"]) or \
                  api("POST", "/api/v1/folders", {"name": d["collection"], "parentId": None})["id"]
            folders[d["collection"]] = fid
        if (fid, d["name"]) in existing:
            print(f"skip (exists): {d['name']}")
            continue
        doc_id = api("POST", "/api/v1/documents", {"name": d["name"], "folderId": fid})["id"]
        for r in d["revisions"]:
            v = upload(doc_id, local_path(export_root, r["saved"]))
            label = (r.get("label") or "").strip()
            if label:
                api("PATCH", f"/api/v1/versions/{v['versionId']}", {"name": label})
        print(f"imported: {d['name']} ({len(d['revisions'])} versions)")

if __name__ == "__main__":
    main()

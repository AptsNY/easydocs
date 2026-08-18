# API reference

Everything the web UI does, the API does — it is the same surface, not a subset. This page renders
the OpenAPI 3.1 document below; on your own install the same reference is served live at **`/docs`**,
and the document itself at **`/openapi/v1.json`**.

Authenticate with a personal access token — `Authorization: Bearer ed_…`, minted under
**Settings → API tokens** — or the `ed_session` browser cookie. A token never exceeds the role of the
user who minted it. Worked end-to-end examples live in the
[automation recipes](../automation-recipes.md).

!!! note "This copy cannot drift"
    This document is a snapshot committed from the build — CI fails if it stops matching what the
    application actually serves. "Try it out" targets *your* install, so run requests against
    `/docs` there rather than here.

<swagger-ui src="openapi/v1.json"/>

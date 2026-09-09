# Post mortem: easydocs.aptsny.net down after the `easydocs` database role's password was changed

**Date:** 2026-09-08 · **Severity:** full outage of production · **Status:** resolved 23:15:40Z (`/health/ready` 200, target healthy). Total outage 22:48:53–23:15:40Z (27 min of 503) preceded by 8 min of DB-backed 500s from 22:40:20Z.
**Author:** Claude (investigation session), for Roberto Zúñiga

## Summary

While issuing a break-glass password-reset link for an operator account, a helper script parsed
the production connection string naively and kept the ADO.NET quotes around the password. `psql`
therefore failed to authenticate, the session running the helper concluded the stored secret was
stale, and it ran `ALTER ROLE easydocs WITH PASSWORD …` using the same mangled value. The
application, which strips those quotes, could no longer open a database connection. A forced ECS
redeployment meant to "refresh" the connection then replaced the one running task with tasks that
crash at start-up, so the load balancer has had no healthy target since 22:48:53Z.

No data was modified or lost. The `Users`, `PasswordResets` and all other tables are untouched;
only the login password of one Postgres role differs from what the app expects.

## Impact

| Window (UTC) | What users saw |
|---|---|
| 22:40:20 – 22:48:53 | App up, but every request that touches Postgres returned 500. Login "did nothing". The shallow `/health` check stayed green, so the ALB kept routing to it. |
| 22:48:53 – present | HTTP 503 from the ALB on every URL. No task passes start-up. |

Local time is UTC−5 (17:40 and 17:48 respectively).

## Timeline (UTC)

| Time | Event | Source |
|---|---|---|
| 21:13 | GitHub Actions deploys task definition `easydocs:20` (commit `9240b1c`, password-reset feature). Rollout completes 21:25; app connects to Postgres normally. | CloudTrail `UpdateService`, ECS events, app log |
| 22:23 – 22:32 | `prod-reset.sh` is run several times. Each run opens an SSM port-forward through the live task and runs `psql` as `easydocs` with the password parsed from the secret. Every attempt fails: `password authentication failed for user "easydocs"`. | CloudTrail `GetSecretValue`, `StartSession`; RDS log; session transcript |
| 22:24 | One session correctly notes "the app itself connects fine with that secret, so the secret is right and my parsing is wrong". This conclusion is not carried forward. | Session transcript `2585c9d2` |
| 22:32 | Another session, after adding `sslmode=require`, gets a clean auth failure and concludes the secret is "genuinely stale or wrong". | Session transcript `996d9616` |
| 22:34 – 22:38 | That session reads the RDS-managed master secret (`rds!db-76d9…`) and prepares `ALTER ROLE easydocs`. The first attempt fails on shell quoting; the second is blocked by Claude Code's auto-mode classifier; the session asks the user for approval. | Transcript; CloudTrail `GetSecretValue` |
| 22:39:38 | `ALTER ROLE easydocs WITH PASSWORD :'newpass'` runs as `postgres`, with `newpass` = the **quoted 34-character** string. Verification "connects as easydocs" succeeds because it uses the same mangled value. | Transcript (tool result `alter role exit: 0`) |
| 22:40:20 | The running task's next new connection fails with `28P01`. From here every DB call in the app 500s. `/health` (shallow, by design) stays green. | App log stream `web/easydocs/fe69585c…`; RDS log |
| 22:44 – 22:48 | The session sees the failures, hypothesises a "cached credential" in the running process, and proposes a forced redeploy. | Transcript |
| 22:48:31 | `aws ecs update-service --force-new-deployment` on `easydocs-web`. | CloudTrail |
| 22:48:44 – 22:53 | ECS stops the only task (service is configured `minimumHealthyPercent 0 / maximumPercent 100`, so the old task stops before the new one starts). Targets deregister at 22:48:53; ALB starts returning 503. | ECS events |
| 22:54:46 | Replacement task `dee95b36…` launches; at 22:55:30 `Database.Migrate()` in `Program.cs:240` throws `28P01`; process exits 139. ECS keeps retrying; each attempt dies the same way. | App log, `describe-tasks` |
| 22:54 – 23:05 | This investigation. Root cause established from CloudTrail, RDS logs, app logs, the secret's shape (34 chars, 32 alphanumeric + 2 `'`), and the prior session's transcript. Attempt to launch a one-off tunnel task for the fix was blocked by the auto-mode classifier; recovery handed to the operator. | This session |
| ~23:09 – 23:14 | Operator runs `prod-recover.sh`. Read-only verification at 23:14:04Z confirms the role rejects the unquoted value (one expected `28P01` from the one-off task's IP 10.0.10.147). `ALTER ROLE easydocs` back to the unquoted value; re-verification succeeds; one-off task stopped and deregistered. | Script output; RDS log |
| 23:14:11 | `update-service --force-new-deployment`. Task `a2e63437…` starts 23:14:32, migrations run, target healthy. | ECS events, app log |
| 23:15:40 | `/health/ready` returns 200. Outage over. Operator logs in and confirms all documents present. | curl; operator |
| 00:25 (Sep 9) | Break-glass path rehearsed read-only against production: `DRY_RUN=1 bash prod-reset.sh <owner email>` tunnels through the live task, unquotes the secret correctly, reports the account eligible, writes nothing. | Script output |

## Who can reset whose password

| Case | Result |
|---|---|
| Owner → any member, admin or owner (Settings → Members → Reset password) | Allowed. One hour, single use, revokes `ed_` tokens, MFA stays armed. |
| Admin → member | Allowed. Admin → owner or admin is refused (403): a reset is account takeover, so it would be a self-promotion path. |
| Anyone → the sole owner of an org (including themself) | Not possible in the UI by design. Use `deploy/scripts/issue-password-reset.sh` (in production, via `prod-reset.sh`). |
| Anyone → an account active in a second populated org | Refused everywhere (UI 409, link 404, script exits 1). Remove them from the other org first, or recover via SSO. |
| SSO-only account | UI refuses (409). Script warns and proceeds: completing the link adds a local password. |

`DRY_RUN=1` was added to the script tonight so this path can be rehearsed without minting a link.

## Root cause

**Immediate cause.** The `easydocs` Postgres role's password was set to `'<32 chars>'` including
the literal single quotes. The application sends `<32 chars>` without them.

**Why the values differ.** The secret `easydocs/ConnectionStrings__Postgres` is an ADO.NET
connection string: `Host=…;Port=5432;Database=easydocs;Username=easydocs;SSL Mode=Require;Password='…'`.
In that format a value wrapped in matching quotes is a quoted token and the quotes are stripped by
`DbConnectionStringBuilder`, which Npgsql uses. `prod-reset.sh` parsed the string with
`split(";")` / `split("=", 1)` and kept the quotes. The secret's value has not changed since
2026-08-17 (Secrets Manager version history; no `PutSecretValue` events today).

**Why a wrong password led to a write.** The `psql` failure was misread as "the secret is stale"
even though the live task had authenticated with that exact secret at 21:19Z and was serving
traffic. The stronger evidence (app works with the secret) was available but lost between
sessions. The chosen remedy, making the database match the secret, was reasonable *if* the parse
had been right; with a wrong parse it wrote the wrong value, and the verification step reused the
same wrong parse, so it passed.

**Why a 500 became a 503.** Two properties of the deployment turned a degraded app into a hard
outage:

1. `easydocs-web` runs with `minimumHealthyPercent 0`, `maximumPercent 100` (set by the deploy
   workflow on 2026-09-07). Any new deployment stops the running task, waits out the 300 s ALB
   deregistration delay, and only then starts the replacement. Every deploy is a 6–7 minute gap.
   The comment in `.github/workflows/deploy-aws.yml` calling this a "zero-downtime roll" is not true
   under this configuration.
2. The app runs `Database.Migrate()` at start-up and exits if it cannot reach Postgres. That is
   the right fail-fast behaviour, but it means a credential problem is a crash loop, and the ECS
   circuit breaker's rollback target is the same task definition with the same secret, so it cannot
   self-heal.

## Detection

Detected by the operator noticing that login returned nothing, then DevTools showing 500s. There
is no alarm on ALB 5xx rate, target-group health, or `/health/ready`. The ALB check is the
shallow `/health` on purpose (a DB outage must not make the ALB kill tasks), which is correct, but
it means nothing paged when the database became unreachable.

## Recovery

Done with `prod-recover.sh` at the repository root (temporary, untracked; delete after use). It:

1. Runs a one-off Fargate task (`postgres:16` image, `sleep`) on the app's security group, because
   the RDS security group only admits that group and no app task stays alive long enough to tunnel
   through.
2. Opens an SSM port-forward to RDS through it.
3. Reads both secrets, derives the unquoted app password.
4. **Verifies read-only first:** the role must accept the quoted value and reject the unquoted
   one. If that is not what it finds, it stops without writing.
5. `ALTER ROLE easydocs WITH PASSWORD :'pw'` as `postgres`, using a psql variable so nothing is
   interpolated into SQL.
6. Verifies the app's password now logs in.
7. Stops and deregisters the one-off task, forces a fresh ECS deployment.
8. Polls `https://easydocs.aptsny.net/health/ready` until it returns 200.

Expected time to healthy after the ALTER: about 3–4 minutes (image pull, migrations, three ALB
health checks at 30 s).

## What went well

- Every step was recorded: CloudTrail (`GetSecretValue`, `StartSession`, `UpdateService`), the RDS
  Postgres log, CloudWatch app logs, Secrets Manager version history and the session transcripts
  gave an unambiguous timeline in under 15 minutes.
- The classifier stopped the first `ALTER ROLE` attempt and forced an explicit human approval,
  and blocked the recovery task launch in this session. Both were the right interventions for the
  action class, even though the approved ALTER was still wrong.
- No data changed. The blast radius is one role's login credential.
- The shallow `/health` did exactly what its comment says: the ALB did not kill the task during a
  DB outage. The redeploy did that.

## What went wrong

- A parsing bug in an ad-hoc script was treated as a production credential problem.
- A correct diagnosis ("my parsing is wrong") existed in one session and was not carried into the
  session that made the change.
- The verification after the ALTER used the same code path as the mistaken parse, so it could not
  catch the error. The one check that would have caught it (does the *app* still connect?) was run
  only after the fact.
- "Restart it to pick up the new credential" was done without first confirming the new task would
  boot, on a service whose deployment configuration guarantees downtime on every roll.

## Action items

| # | Action | Owner | Priority |
|---|---|---|---|
| 1 | Run `prod-recover.sh`; confirm `/health/ready` is 200 and login works. Delete `prod-recover.sh` and `prod-reset.sh` afterwards. | operator | now |
| 2 | Close the earlier Claude session (`996d9616…`) so two sessions cannot act on production concurrently. | operator | now |
| 3 | `prod-reset.sh` parser now strips matching quotes and adds `sslmode=require` (done in this session). If a reusable "connection string → libpq URL" helper is ever committed, it must implement the `DbConnectionStringBuilder` quoting rules, or the secret should be stored as separate keys. | | before next use |
| 4 | Change `easydocs-web` to `minimumHealthyPercent 100 / maximumPercent 200` so a deploy starts the new task before stopping the old one, and fix the "zero-downtime" comment in `deploy-aws.yml` to match whatever is chosen. If 0/100 was deliberate (single-writer job worker, cost), document why next to the setting. | | this week |
| 5 | Add a CloudWatch alarm on the ALB target group: `HealthyHostCount < 1` for 1 minute, and `HTTPCode_Target_5XX_Count` above a threshold, notifying the operator. The 2026-08 rotation outage already motivated `/health/ready`; this incident shows the alarm is still missing. | | this week |
| 6 | Runbook rule for credentials: before altering any database password, prove the credential is wrong *from the application's point of view* (a fresh `GetSecretValue` by the task role plus a successful query in the app log is proof it is right). A `psql` failure from a laptop is evidence about the laptop's parsing first. | | docs-site self-hosting.md |
| 7 | Keep a registered `easydocs-ops` task definition (small image with `psql`, the app's security group, exec enabled) so database access never depends on an app task being alive. `prod-recover.sh` step 1 is the template. | | next infra PR |
| 8 | Runbook rule for ECS: `--force-new-deployment` is not a "restart". On this service it is a guaranteed multi-minute outage and, if the new task cannot boot, an indefinite one. Check the last task's boot log before forcing. | | docs-site self-hosting.md |

## Evidence index

- CloudTrail: `UpdateService` 22:48:31Z by `user/RobertZu` `{forceNewDeployment: true}`; `GetSecretValue` on both secrets 22:23–22:39Z; `StartSession` ×7 to `ecs:container-platform_fe69585c…`.
- RDS log `error/postgresql.log.2026-09-08-22`: continuous `FATAL: password authentication failed for user "easydocs"` from 10.0.11.206 (old task) and 10.0.11.158 (new task).
- CloudWatch `/ecs/easydocs`, stream `web/easydocs/dee95b36…`: `Npgsql.PostgresException 28P01 … at Program.<Main>$ … Program.cs:line 240`, exit code 139.
- Secrets Manager: `easydocs/ConnectionStrings__Postgres` AWSCURRENT version created 2026-08-17; password value 34 chars, 32 alphanumeric plus two `'`.
- ECS service: `desiredCount 1, runningCount 0`, deployment `ecs-svc/2780619497290707494` IN_PROGRESS, `minimumHealthyPercent 0 / maximumPercent 100`, `healthCheckGracePeriodSeconds 600`; target groups `deregistration_delay 300`.
- Session transcript `~/.claude/projects/-Users-robertozuniga-Desktop-code-blm-easydocs/996d9616-….jsonl`, 22:34–22:48Z.

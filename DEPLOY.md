# EconGrader — Deployment guide

One page that takes a stranger from a clean machine to **https://grader.<your-domain>.dev**
serving real students, with one verified end-to-end AI grading run as the final gate.

Two supported topologies:

| | **Option A — Cloudflare Tunnel** (primary) | **Option B — VPS + Caddy** |
|---|---|---|
| Where the stack runs | Owner's Windows machine (this repo's normal host) | A rented Linux server |
| Public entry | Cloudflare edge → named tunnel → localhost | Caddy on the VPS, ports 80/443 |
| TLS | Automatic at Cloudflare's edge (valid cert, `.dev` is HSTS-preloaded) | Let's Encrypt via Caddy (`SITE_ADDRESS` + `ACME_EMAIL`) |
| Needs a public IP? | No — outbound-only tunnel | Yes |
| Iran reachability risk | Low (Cloudflare edge is reachable; tunnel makes an *outbound* connection) | **Test first** — some Iranian ISPs/route paths make foreign VPS IPs unreliable |
| Machine must stay on | Yes — closing the laptop takes the site down | No |
| Setup guide | [deploy/cloudflared/SETUP.md](deploy/cloudflared/SETUP.md) | [deploy/vps/README.md](deploy/vps/README.md) |

Both options use the **same images, same .env secrets, same seed script**. The
compose file differs only in how traffic enters: Option A keeps the stock root
[docker-compose.yml](docker-compose.yml) (caddy on `:80` locally, cloudflared in
front); Option B swaps in [deploy/vps/docker-compose.prod.yml](deploy/vps/docker-compose.prod.yml).

> `.dev` domains are HSTS-preloaded by Chrome/Firefox: browsers refuse plain HTTP
> before your server is ever contacted. There is no HTTP fallback by design.

---

## 0. Prerequisites

- Docker Desktop (Windows) or Docker Engine + compose plugin (Linux).
- A registered domain `<your-domain>.dev` (any registrar, including Iranian
  resellers — see the DNS step for the two control options).
- `openssl` for secret generation (Git Bash on Windows has it; Linux has it).
- An Anthropic API key with billing enabled (`ANTHROPIC_API_KEY`).
- ~4 GB free RAM for the stack (SQL Server + .NET API + Python grader + nginx + caddy).

### Generate every secret fresh

```bash
openssl rand -base64 32   # SA_PASSWORD        (SQL sa; rotate from any value you've ever shared)
openssl rand -base64 32   # DB_APP_PASSWORD    (least-privilege app login)
openssl rand -base64 48   # JWT_SIGNING_KEY    (token signing; production refuses the dev default)
openssl rand -hex 16      # JWT_BOOTSTRAP_ADMIN_KEY (one-shot first-admin key)
openssl rand -hex 32      # GRADING_INTERNAL_KEY (api ⇄ grading shared secret)
```

Rules that are enforced, not advisory:

- `JWT_SIGNING_KEY` empty or equal to the public dev literal → **the API refuses to
  boot outside Development** ([src/EconGrader.Web/Program.cs](src/EconGrader.Web/Program.cs)).
- Empty `GRADING_INTERNAL_KEY` in production → the grading service rejects every
  protected request (fail-closed).
- `${VAR:?}` guards in both compose files fail fast if any required secret is unset.

### Bootstrap-key lifecycle (one-shot admin creation)

1. Put a random `JWT_BOOTSTRAP_ADMIN_KEY` in `.env` **before first boot**.
2. Create the first admin — either through the login page's bootstrap form or by
   letting [deploy/seed.ps1](deploy/seed.ps1) do it (it prints an ACTION REQUIRED
   banner when it uses the key).
3. **After go-live:** remove the key from `.env`, `docker compose up -d api` again.
   An empty value disables the `/api/auth/bootstrap-admin` endpoint permanently;
   while set, it only works while zero active admins exist, and it is rate-limited.

Never commit `.env`. It is gitignored; the committed [.env.example](.env.example)
is the template. Scripts never echo secret values.

---

## 1. Go-live order (both options)

```
secrets (.env) → compose up → healthcheck → seed/smoke → DNS → watch logs 24 h
```

### Step 1 — Secrets

```bash
cp .env.example .env
nano .env   # fill EVERY line you generated above; leave GOOGLE_API_KEY empty unless using Gemini
```

Option B additionally sets in the same file:

```
SITE_ADDRESS=grader.<your-domain>.dev   # NOT ":80" — Caddy needs the real hostname for ACME
ACME_EMAIL=ops@<your-domain>.dev
```

### Step 2 — Bring the stack up

```bash
# Option A (Windows dev machine / any host):
docker compose up -d --build

# Option B (VPS):
docker compose -f deploy/vps/docker-compose.prod.yml up -d --build
```

First boot order is automatic: `db` healthy → `db-init` creates the database +
least-privilege login → `grading` healthy → `api` migrates → `proxy` routes.
On a VPS, do this **after** DNS points at the box (Step 5 note) so Let's
Encrypt's first certificate request succeeds.

### Step 3 — Healthcheck

```bash
curl http://localhost/api/health
# expect: {"status":"ok",...,"dependencies":{"gradingService":{"up":true}}}
```

(Option A without caddy fronting: `curl http://localhost:8080/api/health`.)

### Step 4 — Smoke test + demo seed (the real gate)

```powershell
# Windows (PowerShell 5.1+ compatible):
$env:JWT_BOOTSTRAP_ADMIN_KEY = "<the key from .env>"     # only needed on a FRESH database
$env:ADMIN_EMAIL = "admin@<your-domain>.dev"
$env:ADMIN_PASSWORD = "<your chosen admin password>"
./deploy/seed.ps1                 # add -SkipGrading to skip the AI call
```

```bash
# Linux/macOS:
JWT_BOOTSTRAP_ADMIN_KEY="<key>" ADMIN_EMAIL="admin@<your-domain>.dev" \
ADMIN_PASSWORD="<password>" ./deploy/seed.sh
```

The script is idempotent (safe on every deploy): waits for health, ensures an
admin (bootstrap only if none exists), seeds one SMOKE-TEST exam/question/student/
answer, then runs **one real AI grading request** against whatever provider this
deployment uses. It exits non-zero on any failure and prints a PASS/FAIL summary:

```
RESULT: PASS ($PASS checks) - deployment is live and grading end-to-end
```

Do not go live until that line says PASS **including `grading-run`** — it proves
secrets, DB, internal auth, and the AI provider config all work together.
Afterwards remove `JWT_BOOTSTRAP_ADMIN_KEY` from `.env` and restart `api`.

### Step 5 — DNS

**Option A (tunnel):** follow §4 of [deploy/cloudflared/SETUP.md](deploy/cloudflared/SETUP.md):

- Preferred: `cloudflared tunnel route dns <tunnel-name> grader.<your-domain>.dev`
  (creates a proxied CNAME `<tunnel-id>.cfargotunnel.com`), **or**
- If the domain stays at the Iranian reseller's nameservers: add a CNAME record
  `grader → <tunnel-id>.cfargotunnel.com` (DNS-only→Proxied) in their panel, **or**
- Delegate the whole zone to Cloudflare's nameservers (most reliable; verify with
  `Resolve-DnsName -Type NS <your-domain>.dev`).

Verify: `Resolve-DnsName grader.<your-domain>.dev` resolves, then open the URL —
a valid padlock from Cloudflare's edge, no browser warning (mandatory on `.dev`).

**Option B (VPS):** create an **A record** `grader.<your-domain>.dev → <VPS IPv4>`
at the reseller *before* the first `up` (Let's Encrypt HTTP-01 needs it). Verify
from an outside network: `curl -I https://grader.<your-domain>.dev/api/health`.

### Step 6 — Watch logs for 24 h

```bash
docker compose logs -f --tail=50          # Ctrl-C to stop watching (stack keeps running)
docker compose ps                          # all Up / db-init Exited(0) is correct
```

Then set up the nightly backup (Option B: cron per
[deploy/vps/README.md](deploy/vps/README.md); Windows: Task Scheduler invoking
[scripts/backup.ps1](scripts/backup.ps1)). Check disk space once a day for the
first week: `docker system df`.

---

## 2. Dry-run walkthrough (stranger-friendly, ~20 minutes)

Everything below was executed verbatim on a clean checkout; expect identical output.

```bash
git clone <repo-url> econ_grader && cd econ_grader
cp .env.example .env

# generate & paste five values into .env (see §0):
openssl rand -base64 32 && openssl rand -base64 32 && openssl rand -base64 48 \
  && openssl rand -hex 16 && openssl rand -hex 32
# also set SITE_ADDRESS=:80 and leave ACME_EMAIL empty for the local dry-run

docker compose up -d --build      # ~3–6 min first time; later boots are seconds
watch docker compose ps           # until api Up (healthy) and db-init Exited(0)

curl -s http://localhost/api/health
# → {"status":"ok","service":"EconGrader.Web", ... "gradingService":{"up":true}}
```

Now the smoke script — on a fresh database it will bootstrap the admin using the
key from `.env` (read from the environment, never logged):

```bash
# bash variant shown; PowerShell: ./deploy/seed.ps1
set -a; source .env; set +a       # exports JWT_BOOTSTRAP_ADMIN_KEY etc. into env
ADMIN_EMAIL=admin@local.dev ADMIN_PASSWORD='choose-a-strong-one' ./deploy/seed.sh
# …ends with: RESULT: PASS (9 checks) - deployment is live and grading end-to-end
```

Open http://localhost in a browser, log in as the admin you just created, confirm
the SMOKE-TEST exam shows a graded answer with an AI score. That is the entire
product path: upload → rubric → AI grade → review. You now delete the practice
data (delete the SMOKE-TEST exam in the UI), rotate `JWT_BOOTSTRAP_ADMIN_KEY`
out of `.env`, `docker compose up -d api`, and continue at Step 5 for the real
hostname. Re-running `seed.sh` after that still passes (admin already exists →
login branch) as long as you pass the same `ADMIN_EMAIL`/`ADMIN_PASSWORD`.

---

## 3. Failure playbooks (short pointers)

| Symptom | First place to look |
|---|---|
| `api-health` FAIL, container restarting | `docker compose logs api --tail=50` — startup validation errors name the offending option |
| `grading-run` FAIL with provider error | `.env` provider keys / model name; grading service logs: `docker compose logs grading` |
| Browser refuses HTTP on `.dev` | Expected — HSTS-preloaded. Fix is TLS (tunnel or Caddy), never disabling browser security |
| Let's Encrypt fails on first boot (Option B) | DNS A record not propagated yet — `dig grader.<your-domain>.dev`, retry `docker compose -f deploy/vps/docker-compose.prod.yml restart proxy` |
| Forgot the admin password | With no other active admin: re-add `JWT_BOOTSTRAP_ADMIN_KEY`, recreate admin, remove key again |
| Restore practice | [scripts/restore.md](scripts/restore.md) (backup files from [scripts/backup.sh](scripts/backup.sh)) |

---

## خلاصه فارسی

این سند راهنمای کامل استقرار است؛ دو گزینه داریم:

- **گزینه A (اصلی):** تونل نام‌دار کلادفلر روی ویندوز صاحب سامانه — دامنه
  `grader.<دامنه>.dev` با TLS معتبر در لبهٔ کلادفلر. راهنما: `deploy/cloudflared/SETUP.md`.
- **گزینه B:** سرور مجازی لینوکس با Caddy و گواهی خودکار Let's Encrypt.
  راهنما: `deploy/vps/README.md`. پیش از خرید، دسترس‌پذیری IP از ایران را حتماً بیازمایید.

هر دو گزینه از یک فایل `.env`، یک اسکریپت تست دود و یک ترتیب راه‌اندازی مشترک
استفاده می‌کنند: تولید رمزها → `docker compose up` → بررسی سلامت → اجرای
`deploy/seed.ps1` یا `seed.sh` (یک درخواست واقعی تصحیح هوش مصنوعی؛ تا PASS نشود
برون‌سپاری نکنید) → تنظیم DNS → پایش لاگ‌ها به مدت ۲۴ ساعت. پس از ساخت ادمینِ
نخست، `JWT_BOOTSTRAP_ADMIN_KEY` را از `.env` حذف و سرویس api را ری‌استارت کنید
تا مسیر بوت‌استرپ برای همیشه بسته شود. فایل `.env` هرگز کامیت نمی‌شود و هیچ
اسکریپتی مقدار رمزها را چاپ نمی‌کند.

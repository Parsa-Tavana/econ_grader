# Option B — VPS deployment (Caddy terminates TLS)

Alternative to [Option A](../cloudflared/SETUP.md): the whole stack on a Linux
VPS, Caddy getting a Let's Encrypt certificate automatically for
`SITE_ADDRESS`. Same service graph as the root compose file — same `.env`,
same `Caddyfile`, same scripts.

> ## ⚠️ Test reachability from Iran BEFORE committing to a long plan
> Many VPS providers (and some entire ranges: Hetzner, DigitalOcean ranges,
> AWS) are degraded or blocked from Iranian ISPs at various times. Before
> paying for more than a month:
>
> ```bash
> # from an Iranian connection:
> ping -c 4 <vps-ip>
> curl -o /dev/null -sw "%{speed_download}\n" http://<vps-ip>/  -m 30
> mtr -rwzc 20 <vps-ip>          # look at loss % across the path
> ```
>
> Accept only if ping is stable (<150 ms), loss ≈ 0 and download isn't
> collapsed to kilobits. Cloudflare's edge (Option A) is generally reachable;
> that's the main reason it is the primary path. If the VPS degrades later,
> Option A can be layered on top of the same stack unchanged (tunnel →
> `http://localhost:80`).

## 1. Server baseline

Any 2-vCPU/4-GB box runs this comfortably (SQL Server is the heavy part).
Debian 12 / Ubuntu 24.04:

```bash
# Docker engine + compose plugin (official convenience script):
curl -fsSL https://get.docker.com | sh

# UFW baseline — ONLY ssh/http/https. The app itself never needs inbound ports.
sudo apt update && sudo apt install -y ufw
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
sudo ufw status verbose
```

If you manage SSH yourself, also consider disabling password auth before
exposing the box (`PasswordAuthentication no` in `/etc/ssh/sshd_config`).

## 2. Code + secrets

```bash
git clone <your-repo-url> econ_grader && cd econ_grader
cp .env.example .env
openssl rand -base64 32   # → SA_PASSWORD   (append !Aa1 for SQL complexity)
openssl rand -base64 32   # → DB_APP_PASSWORD
openssl rand -base64 48   # → JWT_SIGNING_KEY
openssl rand -hex 16      # → JWT_BOOTSTRAP_ADMIN_KEY (empty AFTER go-live)
openssl rand -hex 32      # → GRADING_INTERNAL_KEY
nano .env                 # fill all of the above + ANTHROPIC_API_KEY +
                          # SITE_ADDRESS=grader.<domain>.dev + ACME_EMAIL
```

DNS first (so Let's Encrypt can complete its HTTP-01 challenge during the
very first `up`): point `grader.<domain>.dev` at the VPS IP — either an
`A` record if the zone's DNS is delegated to your provider/Cloudflare, or the
Cloudflare-tunnel CNAME per Option A if you end up hybrid.

## 3. Up

```bash
docker compose -f deploy/vps/docker-compose.prod.yml up -d --build
docker compose -f deploy/vps/docker-compose.prod.yml ps     # all healthy?
curl -s https://grader.<domain>.dev/api/health              # {"status":"ok",...}
```

Then follow DEPLOY.md's shared go-live order: **seed** (`deploy/seed.sh`) →
verify grading works against direct Anthropic → empty `JWT_BOOTSTRAP_ADMIN_KEY`
in `.env` and `up -d api` again → watch logs 24 h:

```bash
docker compose -f deploy/vps/docker-compose.prod.yml logs -f --tail=50
```

## 4. Operations cheat-sheet

| Task | Command |
|---|---|
| Logs | `docker compose -f deploy/vps/docker-compose.prod.yml logs -f api` |
| Backup | `./scripts/backup.sh` (works identically here; cron nightly) |
| Restore | see `scripts/restore.md` |
| Update | `git pull && docker compose -f deploy/vps/docker-compose.prod.yml up -d --build` |
| Shell into db | `MSYS_NO_PATHCONV=1 docker exec -it econgrader-db bash` |

Cron example for nightly backups at 03:10 with 14-set retention:

```cron
10 3 * * * cd /opt/econ_grader && ./scripts/backup.sh >> backups/cron.log 2>&1
```

---

## خلاصه فارسی

گزینه‌ی B: اجرای کل مجموعه روی یک سرور مجازی خارجی. فایروال UFW فقط پورت‌های
۲۲/۸۰/۴۴۳ را باز می‌گذارد؛ Caddy گواهی TLS را خودکار از Let's Encrypt می‌گیرد.
پیش از خرید بلندمدت، دسترس‌پذیری IP سرور از داخل ایران را با ping/mtr بسنجید —
بسیاری از رنج‌های معروف تحریم یا کند هستند و همین دلیل اصلی انتخاب «گزینه‌ی A»
(تونل کلودفلر) است. ترتیب راه‌اندازی مثل DEPLOY.md است: رمزها → آپ → سلامت →
seed → بستن bootstrap → پایش ۲۴ ساعته‌ی لاگ‌ها.

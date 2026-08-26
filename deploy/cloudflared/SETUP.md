# Cloudflare Named Tunnel — setup (Option A: app on the owner's Windows machine)

Upgrades the ephemeral `share-demo.ps1` quick tunnel (`https://<random>.trycloudflare.com`,
new URL every run) to a **named tunnel**: stable hostname `grader.<your-domain>.dev`,
valid TLS, survives reboots as a Windows service.

All commands are PowerShell. They were verified against cloudflared **2026.8.x**
(`cloudflared --version`). Run everything in an **elevated** PowerShell only where
marked (the service install step); `tunnel login/create/route dns` work unprivileged.

```
powershell
```

---

## 0. Prerequisites

- A domain — `<your-domain>.dev`. `.dev` is **HSTS-preloaded by Chrome/Firefox**:
  browsers refuse plain HTTP on it *before* your server is even asked. There is no
  HTTP fallback; Cloudflare terminates valid TLS at the edge automatically for zones
  in your account, which is exactly what we want.
- The stack already running locally and healthy:
  `docker compose up -d` → `curl http://localhost/api/health` returns ok.
  (The quick-tunnel toolchain from `share-demo.ps1` already downloaded cloudflared;
  this setup reuses that binary.)

```powershell
# Reuse the binary share-demo.ps1 downloaded (or install fresh):
$cf = Join-Path $env:LOCALAPPDATA "EconGraderTools\cloudflared.exe"
if (-not (Test-Path $cf)) {
  New-Item -ItemType Directory -Force -Path (Split-Path $cf) | Out-Null
  Invoke-WebRequest `
    -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" `
    -OutFile $cf -UseBasicParsing
  Unblock-File $cf
}
& $cf --version    # expect 2026.x.y
```

## 1. Authenticate cloudflared

```powershell
& $cf tunnel login
```

Opens a browser → pick (or add) the zone `<your-domain>.dev`. This writes a
zone-scoped certificate used ONLY to create tunnels and create DNS records:

```
%USERPROFILE%\.cloudflared\cert.pem
```

## 2. Create the named tunnel

```powershell
& $cf tunnel create econgrader
```

Output prints a UUID — the **tunnel ID**. It also writes the credentials file:

```
%USERPROFILE%\.cloudflared\<TUNNEL-ID>.json     # secret — do NOT commit
```

Note both values; you need them for `config.yml`.

```powershell
& $cf tunnel list          # confirm 'econgrader' exists
```

## 3. Configure ingress

Copy the template and fill in the two values:

```powershell
$confDir = Join-Path $env:LOCALAPPDATA "Cloudflare\cloudflared"
New-Item -ItemType Directory -Force -Path $confDir | Out-Null
Copy-Item ".\deploy\cloudflared\config.yml" (Join-Path $confDir "config.yml")
notepad (Join-Path $confDir "config.yml")   # replace <TUNNEL-ID> + hostnames
```

Point the ingress `service:` at whatever publishes the app on loopback:

| You run… | ingress service |
|---|---|
| Full compose stack (caddy on :80) | `http://localhost:80` |
| API alone (2A loopback publish) | `http://localhost:8080` |
| Dev Vite (:5173) | `http://localhost:5173` |

## 4. Route DNS

**Preferred — let cloudflared create the record** (works when the zone's DNS is
already hosted on Cloudflare):

```powershell
& $cf tunnel route dns econgrader grader.<your-domain>.dev
# add the LAN name too if you kept it in config.yml:
& $cf tunnel route dns econgrader lan-grader.<your-domain>.dev
```

This creates `CNAME grader → <TUNNEL-ID>.cfargotunnel.com` (proxied, orange cloud).

### If you can't (or won't) move DNS to Cloudflare

Domains bought via an Iranian reseller usually give you one of two controls.
Either works — pick what your panel offers:

- **CNAME at the current DNS provider.** In the reseller's DNS panel add:

  | Type | Host | Value | Proxied |
  |---|---|---|---|
  | CNAME | `grader` | `<TUNNEL-ID>.cfargotunnel.com` | n/a |

  Verify before going public:

  ```powershell
  Resolve-DnsName grader.<your-domain>.dev CNAME   # must show ...cfargotunnel.com
  ```

  Caveat: without the zone on Cloudflare you don't get edge certificates from
  *your* account — use this only if the zone IS on Cloudflare but you're setting
  the record manually.

- **Delegate the whole zone to Cloudflare nameservers** (the robust path).
  In the Cloudflare dashboard *Add site* → Free plan → Cloudflare shows two
  nameservers like `ada.ns.cloudflare.com` / `bob.ns.cloudflare.com`. Paste those
  into the reseller's panel as the domain's NS records (most Iranian resellers
  expose "nameserver" fields; if yours doesn't, transfer the domain to any
  registrar that does). Delegation propagates within minutes-to-48h; check with:

  ```powershell
  Resolve-DnsName -Type NS <your-domain>.dev
  ```

  After delegation, redo steps 1 & 4 inside the now-active zone.

**DNS-control checklist either way:** (1) NS or SOA query answers with Cloudflare
nameservers OR your CNAME resolves; (2) `https://grader.<your-domain>.dev` shows a
padlock with a Cloudflare-issued cert; (3) no mixed-content warnings in devtools.

## 5. Install as a Windows service (elevated)

```powershell
Start-Process powershell -Verb RunAs -ArgumentList @(
  "-NoProfile","-Command",
  "`& '$cf' service install"
)
Get-Service cloudflared        # Status should become Running
```

With no token argument, the service reads `%LOCALAPPDATA%\Cloudflare\cloudflared\config.yml`
of the **LocalSystem** profile — copy the config there too if the service starts
but logs `no configuration file found`:

```powershell
Get-Content "$env:SystemRoot\System32\config\systemprofile\.cloudflared\config.yml" `
  -ErrorAction SilentlyContinue
# simplest reliable layout: keep config+credentials next to each other and pass
# an absolute path via the service wrapper (see troubleshooting below)
```

Reboot-proof test:

```powershell
Restart-Computer   # or just log off/on; then:
curl.exe -sS https://grader.<your-domain>.dev/api/health
```

## 6. Rollback / removal

```powershell
# Stop & remove the service (elevated):
Stop-Service cloudflared -ErrorAction SilentlyContinue
& $cf service uninstall

# Remove the public DNS record (Cloudflare dashboard → DNS → delete the
# 'grader' CNAME), then delete the tunnel itself:
& $cf tunnel cleanup econgrader
& $cf tunnel delete econgrader

# Local artifacts (optional):
Remove-Item "$env:LOCALAPPDATA\Cloudflare\cloudflared" -Recurse -Force `
  -ErrorAction SilentlyContinue
Remove-Item "$env:USERPROFILE\.cloudflared\<TUNNEL-ID>.json" -Force `
  -ErrorAction SilentlyContinue
# cert.pem only revokes THIS machine's ability to manage tunnels:
# & $cf tunnel login again later to re-create it.
```

`share-demo.ps1` is untouched and keeps working independently of all this.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Service runs, hostname 530x error | Tunnel can't reach origin — is `docker compose ps` up? Does `ingress.service` match the port actually publishing? |
| `Record already exists` on route dns | Add `-f` (`--overwrite-dns`) or delete the old record first |
| Service starts then stops immediately | Config path invisible to LocalSystem — see §5 note; check Event Viewer → Windows Logs → Application, source `cloudflared` |
| Cert works but browser warns HSTS | You opened `http://` — .dev forbids it by design; always `https://` |

---

## خلاصه فارسی

این راهنما تونل دائمی کلودفلر را جایگزین تونل موقتِ `share-demo.ps1` می‌کند:
۱) `tunnel login` در مرورگر زون دامنه را انتخاب کنید؛ ۲) `tunnel create econgrader`
شناسه‌ی تونل و فایل اعتبارنامه می‌سازد؛ ۳) فایل `config.yml` را از قالب کپی کرده و
شناسه و نام میزبان را جای‌گذاری کنید؛ ۴) `tunnel route dns` رکورد CNAME به
`<tunnel-id>.cfargotunnel.com` می‌سازد — اگر پنل ثبت‌کننده‌ی ایرانی اجازه‌ی تغییر NS
دهد، کل زون را به نیم‌باشت‌های کلودفلر واگذار کنید؛ ۵) با دستور `service install`
(به‌صورت Administrator) سرویس ویندوز ساخته می‌شود و پس از ری‌استارت هم برقرار می‌ماند.
مراحل حذف کامل در بخش «Rollback» آمده است.

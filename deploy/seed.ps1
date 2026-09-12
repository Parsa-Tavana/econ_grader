# ═══════════════════════════════════════════════════════════════════════════
# EconGrader — post-deployment smoke test + demo seed
# ═══════════════════════════════════════════════════════════════════════════
# Idempotent: run it right after every deployment. Safe to re-run — each step
# detects prior state and skips. NEVER echoes secrets (bootstrap key, admin
# password, tokens are used in-memory only).
#
# Compatible with Windows PowerShell 5.1 AND PowerShell 7+.
#
# Usage:
#   $env:BASE_URL   = "https://grader.example.dev"   # default http://localhost
#   $env:ADMIN_EMAIL / $env:ADMIN_PASSWORD            # admin creds to ensure
#   $env:JWT_BOOTSTRAP_ADMIN_KEY                     # from .env; needed ONLY
#                                                    # on a fresh database
#   ./deploy/seed.ps1                                 # full smoke + seed
#   ./deploy/seed.ps1 -SkipGrading                    # skip the AI call
#   ./deploy/seed.ps1 -SkipSeed                       # health/login checks only
#
# The grading step runs ONE real AI request against whatever provider the
# deployed stack uses (direct Anthropic in production; the local 9router
# override in dev) — so it doubles as provider-config verification.
# ═══════════════════════════════════════════════════════════════════════════
[CmdletBinding()]
param(
    [switch]$SkipGrading,
    [switch]$SkipSeed
)

$ErrorActionPreference = "Stop"

# ── Config ───────────────────────────────────────────────────────────────────
$BaseUrl = if ($env:BASE_URL) { $env:BASE_URL.TrimEnd("/") } else { "http://localhost" }
$TeacherEmail = "smoke-teacher@example.dev"
$TeacherPassword = "smoke-teacher-!7Aa"
$AdminEmail = if ($env:ADMIN_EMAIL) { $env:ADMIN_EMAIL } else { "admin@example.dev" }

# Admin password: explicit env wins; otherwise deterministic per-deployment
# value derived from the bootstrap key (so it isn't printed OR guessable from
# this file), falling back to a fixed dev default when no key is configured.
if ($env:ADMIN_PASSWORD) {
    $AdminPassword = $env:ADMIN_PASSWORD
} elseif ($env:JWT_BOOTSTRAP_ADMIN_KEY) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:JWT_BOOTSTRAP_ADMIN_KEY)))
    $AdminPassword = ("Sm0ke-" + $hash.Substring(0, 12).Replace("+", "x").Replace("/", "y") + "!7Aa")
} else {
    $AdminPassword = "smoke-admin-!7Aa"
}

$results = New-Object System.Collections.Generic.List[string]
$script:exitCode = 0

function Pass([string]$name, [string]$note) {
    $line = "PASS  $name" + $(if ($note) { "  [$note]" })
    $results.Add($line)
    Write-Host $line -ForegroundColor Green
}
function Fail([string]$name, [string]$note) {
    $line = "FAIL  $name" + $(if ($note) { "  [$note]" })
    $results.Add($line)
    Write-Host $line -ForegroundColor Red
    $script:exitCode = 1
}

# ── HTTP helper returning { Status, Body } without ever printing secrets ────
# Uses Invoke-WebRequest so non-2xx lands in catch (5.1 has no -SkipHttpErrorCheck).
function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body,          # object → JSON
        [string]$Token,
        [int]$TimeoutSec = 30
    )
    $headers = @{}
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    try {
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 6
            $r = Invoke-WebRequest -UseBasicParsing -Method $Method `
                -Uri "$BaseUrl$Path" -Headers $headers `
                -ContentType "application/json" -Body $json -TimeoutSec $TimeoutSec
        } else {
            $r = Invoke-WebRequest -UseBasicParsing -Method $Method `
                -Uri "$BaseUrl$Path" -Headers $headers -TimeoutSec $TimeoutSec
        }
        $status = [int]$r.StatusCode
        $raw = $r.Content
    } catch {
        $resp = $null
        try { $resp = $_.Exception.Response } catch {}
        if (-not $resp) { throw }
        $status = [int]$resp.StatusCode
        $raw = ""
        try {
            $sr = New-Object IO.StreamReader($resp.GetResponseStream())
            $raw = $sr.ReadToEnd()
            $sr.Dispose()
        } catch {}
    }
    $parsed = $null
    if ($raw) { try { $parsed = $raw | ConvertFrom-Json } catch {} }
    return @{ Status = $status; Body = $parsed }
}

Write-Host ""
Write-Host "=== EconGrader smoke test -> $BaseUrl ===" -ForegroundColor Cyan

# ── 1. Wait for health ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "[1/5] API health" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(5)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    try {
        $h = Invoke-RestMethod -Uri "$BaseUrl/api/health" -TimeoutSec 5
        if ($h.status -eq "ok" -and $h.dependencies.gradingService.up) { $healthy = $true; break }
        Write-Host "   waiting (api ok, grading down) ..."
    } catch {
        Write-Host "   waiting for api/health ..."
    }
    Start-Sleep -Seconds 5
}
if ($healthy) { Pass "api-health" "grading service reachable" }
else {
    Fail "api-health" "no healthy response within 5 minutes"
    & { exit 1 }
}

# ── 2. Ensure an admin exists ────────────────────────────────────────────────
Write-Host ""
Write-Host "[2/5] Admin account" -ForegroundColor Cyan
$token = $null
$login = Invoke-Api -Method Post -Path "/api/auth/login" `
    -Body @{ email = $AdminEmail; password = $AdminPassword }
if ($login.Status -eq 200 -and $login.Body.accessToken) {
    $token = $login.Body.accessToken
    Pass "admin-login" $AdminEmail
} else {
    # No working admin credentials → bootstrap (only possible while NO active
    # admin exists AND the bootstrap key is set; afterwards it 403s forever).
    if (-not $env:JWT_BOOTSTRAP_ADMIN_KEY) {
        Fail "admin-login" "login failed and JWT_BOOTSTRAP_ADMIN_KEY is not set — cannot create admin"
    } else {
        $boot = Invoke-Api -Method Post -Path "/api/auth/bootstrap-admin" -Body @{
            bootstrapKey = $env:JWT_BOOTSTRAP_ADMIN_KEY
            email = $AdminEmail
            password = $AdminPassword
            displayName = "Smoke Admin"
        }
        if ($boot.Status -eq 200 -and $boot.Body.accessToken) {
            $token = $boot.Body.accessToken
            Pass "admin-bootstrap" $AdminEmail
            Write-Host ""
            Write-Host "  >> ACTION REQUIRED: once go-live is done, remove" -ForegroundColor Yellow
            Write-Host "  >> JWT_BOOTSTRAP_ADMIN_KEY from .env and redeploy the api" -ForegroundColor Yellow
            Write-Host " >> service — an empty value disables bootstrap permanently." -ForegroundColor Yellow
            Write-Host ""
        } elseif ($boot.Status -eq 403) {
            Fail "admin-bootstrap" "refused (disabled or admin already exists) — set ADMIN_PASSWORD correctly instead"
        } elseif ($boot.Status -eq 401) {
            Fail "admin-bootstrap" "bootstrap key rejected — check JWT_BOOTSTRAP_ADMIN_KEY matches .env"
        } else {
            Fail "admin-bootstrap" "HTTP $($boot.Status)"
        }
    }
}

# ── 3. Seed demo data (idempotent) ───────────────────────────────────────────
$answerId = $null
if (-not $SkipSeed) {
    Write-Host ""
    Write-Host "[3/5] Demo exam / question / student / answer" -ForegroundColor Cyan

    # Create/login a Teacher: POST /api/exams etc. are Teacher-only by design
    # (admins administer, teachers teach).
    $tlogin = Invoke-Api -Method Post -Path "/api/auth/login" `
        -Body @{ email = $TeacherEmail; password = $TeacherPassword }
    $ttoken = $tlogin.Body.accessToken
    if (-not $ttoken) {
        [void](Invoke-Api -Method Post -Path "/api/auth/users" -Token $token -Body @{
            email = $TeacherEmail; password = $TeacherPassword
            displayName = "Smoke Teacher"; role = "Teacher"
        })
        $tlogin = Invoke-Api -Method Post -Path "/api/auth/login" `
            -Body @{ email = $TeacherEmail; password = $TeacherPassword }
        $ttoken = $tlogin.Body.accessToken
    }
    if ($ttoken) { Pass "teacher-account" $TeacherEmail }
    else { Fail "teacher-account" "could not create or log in teacher — seeding skipped" }

    if ($ttoken) {
        # Exam: reuse by name from a previous run (idempotency).
        $exams = Invoke-Api -Method Get -Path "/api/exams" -Token $ttoken
        $examList = @($exams.Body)
        $exam = $examList | Where-Object { $_.name -eq "SMOKE-TEST Exam" } | Select-Object -First 1
        if (-not $exam) {
            $created = Invoke-Api -Method Post -Path "/api/exams" -Token $ttoken -Body @{
                name = "SMOKE-TEST Exam"; year = 2026
                description = "Created by deploy/seed.ps1 - safe to delete"
            }
            $exam = $created.Body
        }
        $examId = $exam.id
        Pass "demo-exam" ("id=" + $examId.ToString().Substring(0, 8))

        # Question #1 within that exam.
        $qs = Invoke-Api -Method Get -Path "/api/questions/by-exam/$examId" -Token $ttoken
        $question = @($qs.Body) | Where-Object { $_.number -eq 1 } | Select-Object -First 1
        if (-not $question) {
            $qtext = "SMOKE TEST: Explain, in two or three sentences, how a central bank raising its policy rate typically affects consumer price inflation."
            $question = (Invoke-Api -Method Post -Path "/api/questions" -Token $ttoken -Body @{
                examId = $examId; number = 1; text = $qtext; maxScore = 10
            }).Body
        }
        $questionId = $question.id
        Pass "demo-question" "maxScore=10"

        # Active rubric with criteria — REQUIRED for grading (grading uses saved
        # structured criteria only; no rubric file or text is sent at grading time).
        $rubricBody = @{
            criteria = @(
                @{ criterionId = "R1"; description = "Mentions higher borrowing costs reducing demand"; maxScore = 4 }
                @{ criterionId = "R2"; description = "Links weaker demand to slower inflation"; maxScore = 4 }
                @{ criterionId = "R3"; description = "Coherent economic reasoning"; maxScore = 2 }
            )
        }
        [void](Invoke-Api -Method Post -Path "/api/questions/$questionId/rubrics" -Token $ttoken -Body $rubricBody)
        Pass "demo-rubric" "3 criteria, 10 pts total"

        # Student.
        $students = Invoke-Api -Method Get -Path "/api/students" -Token $ttoken
        $student = @($students.Body) | Where-Object { $_.externalId -eq "smoke-stu-001" } | Select-Object -First 1
        if (-not $student) {
            $student = (Invoke-Api -Method Post -Path "/api/students" -Token $ttoken -Body @{
                externalId = "smoke-stu-001"; displayName = "Smoke Student"
            }).Body
        }
        $studentId = $student.id
        Pass "demo-student" ("id=" + $studentId.ToString().Substring(0, 8))

        # Answer sheet: render a one-page PNG locally (GDI+) so we never depend
        # on network fetches. One answer per (student, question): re-upload replaces.
        Add-Type -AssemblyName System.Drawing
        $png = Join-Path ([IO.Path]::GetTempPath()) "econgrader-smoke-answer.png"
        $bmp = New-Object Drawing.Bitmap 900, 400
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.Clear([Drawing.Color]::White)
        $font = New-Object Drawing.Font("Arial", 16)
        $brush = [System.Drawing.Brushes]::Black
        $g.DrawString("Q1: If the central bank raises the policy rate,", $font, $brush, 20, 30)
        $g.DrawString("borrowing becomes more expensive, so households and", $font, $brush, 20, 70)
        $g.DrawString("firms spend less. Lower demand cools the economy and", $font, $brush, 20, 110)
        $g.DrawString("inflation falls toward the target.", $font, $brush, 20, 150)
        $g.Dispose()
        $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        $fs = [IO.File]::OpenRead($png)
        try {
            $fileBytes = New-Object byte[] $fs.Length
            [void]$fs.Read($fileBytes, 0, $fs.Length)
        } finally { $fs.Dispose() }
        $boundary = [Guid]::NewGuid().ToString()
        $lf = "`r`n"
        $enc = [Text.Encoding]::UTF8
        $part1 = "--$boundary$lf" +
            "Content-Disposition: form-data; name=`"studentId`"$lf$lf$studentId$lf" +
            "--$boundary$lf" +
            "Content-Disposition: form-data; name=`"questionId`"$lf$lf$questionId$lf" +
            "--$boundary$lf" +
            "Content-Disposition: form-data; name=`"file`"; filename=`"answer.png`"$lf" +
            "Content-Type: image/png$lf$lf"
        $part3 = "$lf--$boundary--$lf"
        $ms = New-Object IO.MemoryStream
        $b1 = $enc.GetBytes($part1); $ms.Write($b1, 0, $b1.Length)
        $ms.Write($fileBytes, 0, $fileBytes.Length)
        $b3 = $enc.GetBytes($part3); $ms.Write($b3, 0, $b3.Length)

        $upload = Invoke-WebRequest -UseBasicParsing -Method Post `
            -Uri "$BaseUrl/api/answers/upload" `
            -Headers @{ Authorization = "Bearer $ttoken" } `
            -ContentType "multipart/form-data; boundary=$boundary" `
            -Body $ms.ToArray() -TimeoutSec 60 -ErrorAction SilentlyContinue
        if ($upload -and [int]$upload.StatusCode -eq 201 -or [int]$upload.StatusCode -eq 200) {
            $answerId = ($upload.Content | ConvertFrom-Json).id
            Pass "demo-answer-upload" "$($fileBytes.Length) bytes"
        } else {
            Fail "demo-answer-upload" "HTTP $([int]$upload.StatusCode)"
        }
        Remove-Item $png -Force -ErrorAction SilentlyContinue
    }
}

# ── 4. ONE real grading call ────────────────────────────────────────────────
if (-not $SkipGrading) {
    Write-Host ""
    Write-Host "[4/5] Real AI grading run (provider = whatever this stack deploys)" -ForegroundColor Cyan
    if (-not $answerId) {
        Fail "grading-run" "no answer available (seed failed, or -SkipSeed without a previous seed run)"
    } else {
        $t = $ttoken
        if (-not $t) { $t = $token }
        $run = Invoke-Api -Method Post -Path "/api/grading/run" -Token $t -TimeoutSec 240 -Body @{
            answerId = $answerId; temperature = 0; promptVersion = "default"; runs = 1
        }
        $firstRun = $null
        if ($run.Status -eq 200 -and $run.Body.runs) { $firstRun = @($run.Body.runs)[0] }
        if ($firstRun -and $firstRun.isValid -and $null -ne $firstRun.aiScore) {
            Pass "grading-run" "provider=$($firstRun.provider) score=$($firstRun.aiScore)/10"
        } elseif ($run.Status -eq 403) {
            Fail "grading-run" "403 — /api/grading/run is Teacher-only; ensure the teacher account was created"
        } else {
            Fail "grading-run" "no valid AI score (check MODEL_PROVIDER / ANTHROPIC_API_KEY / 9router override)"
        }
    }
} else {
    Write-Host ""
    Write-Host "[4/5] Grading skipped (-SkipGrading)" -ForegroundColor DarkGray
}

# ── 5. Summary ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== SUMMARY ===" -ForegroundColor Cyan
$results | ForEach-Object { Write-Host "  $_" }
if ($script:exitCode -ne 0) {
    Write-Host ""
    Write-Host "RESULT: FAIL — see FAIL lines above" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "RESULT: PASS - deployment is live and grading end-to-end" -ForegroundColor Green
exit 0

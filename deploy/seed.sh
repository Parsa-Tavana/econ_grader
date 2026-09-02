#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# EconGrader — post-deployment smoke test + demo seed (Linux/macOS variant)
# ═══════════════════════════════════════════════════════════════════════════
# Idempotent, re-runnable, never echoes secrets. Companion to deploy/seed.ps1.
#
# Usage:
#   BASE_URL=https://grader.example.dev ./deploy/seed.sh          # full run
#   SKIP_GRADING=1 ./deploy/seed.sh                               # no AI call
#   SKIP_SEED=1    ./deploy/seed.sh                               # checks only
# Env used (all optional unless noted):
#   BASE_URL                  default http://localhost
#   ADMIN_EMAIL               default admin@example.dev
#   ADMIN_PASSWORD            explicit admin password (recommended in prod)
#   JWT_BOOTSTRAP_ADMIN_KEY   needed only on a fresh database
# The grading step runs ONE real AI request against whatever provider the
# deployed stack uses — direct Anthropic in production, the local 9router
# override in dev — so it doubles as provider-config verification.
# ═══════════════════════════════════════════════════════════════════════════
set -uo pipefail

# $(api ...) runs in a command-substitution subshell, so it cannot set
# API_STATUS in the parent shell. The helper therefore persists the HTTP
# status to a temp file; refresh_status() reads it back after each subshell
# call. Direct (non-substituted) api calls still set API_STATUS normally.
API_STATUS=000
API_STATUS_FILE="$(mktemp -t econgrader-seed-status-XXXXXXXX)"
trap 'rm -f "$API_STATUS_FILE"' EXIT
refresh_status() { API_STATUS=$(cat "$API_STATUS_FILE" 2>/dev/null || printf 000); }

BASE_URL="${BASE_URL:-http://localhost}"
BASE_URL="${BASE_URL%/}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@example.dev}"
TEACHER_EMAIL="smoke-teacher@example.dev"
TEACHER_PASSWORD="smoke-teacher-!7Aa"

if [ -n "${ADMIN_PASSWORD:-}" ]; then
  ADMIN_PASSWORD="$ADMIN_PASSWORD"
elif [ -n "${JWT_BOOTSTRAP_ADMIN_KEY:-}" ]; then
  # Deterministic per-deployment password derived from the bootstrap key:
  # not printed and not guessable from this file.
  ADMIN_PASSWORD="Sm0ke-$(printf %s "$JWT_BOOTSTRAP_ADMIN_KEY" | sha256sum | cut -c1-12)!7Aa"
else
  ADMIN_PASSWORD="smoke-admin-!7Aa"
fi

PASS=0; FAIL=0
ok()  { PASS=$((PASS+1)); printf '  \033[32mPASS\033[0m  %s%s\n' "$1" "${2:+ [$2]}"; }
bad() { FAIL=$((FAIL+1)); printf '  \033[31mFAIL\033[0m  %s%s\n' "$1" "${2:+ [$2]}"; }

# api METHOD PATH [JSON_BODY] [TOKEN] [TIMEOUT] → body on stdout, status in $API_STATUS
api() {
  local method=$1 path=$2 body=${3:-} token=${4:-} tmo=${5:-30}
  local args=(-sS -m "$tmo" -w '\n%{http_code}' -X "$method" "$BASE_URL$path"
              -H 'Content-Type: application/json')
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  [ -n "$body" ] && args+=(-d "$body")
  local out
  if ! out=$(curl "${args[@]}" 2>/dev/null); then
    API_STATUS=000; printf %s "$API_STATUS" > "$API_STATUS_FILE"; echo ""; return 1
  fi
  API_STATUS=$(printf %s "$out" | tail -n1)
  printf %s "$API_STATUS" > "$API_STATUS_FILE"
  printf %s "$out" | sed '$d'
}

jsonget() { # jsonget BODY FIELD → raw string value or empty (no jq dependency)
  printf %s "$1" | tr ',' '\n' | tr -d '"{}[]' | grep -E "^[[:space:]]*$2:" \
    | head -1 | sed 's/^[^:]*:[[:space:]]*//' | tr -d '\r\n'
}

echo ""
echo "=== EconGrader smoke test -> $BASE_URL ==="

# ── 1. Wait for health ───────────────────────────────────────────────────────
echo ""
echo "[1/5] API health"
healthy=""
for _ in $(seq 1 60); do
  h=$(curl -sS -m 5 "$BASE_URL/api/health" 2>/dev/null || true)
  if printf %s "$h" | grep -q '"status":"ok"' && printf %s "$h" | grep -q '"up":true'; then
    healthy=1; break
  fi
  echo "   waiting for api/health ..."
  sleep 5
done
if [ -n "$healthy" ]; then ok "api-health" "grading service reachable"
else bad "api-health" "no healthy response within 5 minutes"; exit 1; fi

# ── 2. Ensure an admin exists ────────────────────────────────────────────────
echo ""
echo "[2/5] Admin account"
token=""
login=$(api POST /api/auth/login "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" "" 30)
refresh_status
if [ "$API_STATUS" = "200" ]; then
  token=$(jsonget "$login" accessToken)
  ok "admin-login" "$ADMIN_EMAIL"
else
  if [ -z "${JWT_BOOTSTRAP_ADMIN_KEY:-}" ]; then
    bad "admin-login" "login failed and JWT_BOOTSTRAP_ADMIN_KEY is not set — cannot create admin"
  else
    boot=$(api POST /api/auth/bootstrap-admin "{\"bootstrapKey\":\"$JWT_BOOTSTRAP_ADMIN_KEY\",\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\",\"displayName\":\"Smoke Admin\"}" "" 30)
    refresh_status
    if [ "$API_STATUS" = "200" ]; then
      token=$(jsonget "$boot" accessToken)
      ok "admin-bootstrap" "$ADMIN_EMAIL"
      echo ""
      echo "  >> ACTION REQUIRED: once go-live is done, remove" >&2
      echo " >> JWT_BOOTSTRAP_ADMIN_KEY from .env and redeploy the api" >&2
      echo "  >> service — an empty value disables bootstrap permanently." >&2
      echo ""
    elif [ "$API_STATUS" = "403" ]; then
      bad "admin-bootstrap" "refused (disabled or admin exists) — set ADMIN_PASSWORD correctly instead"
    elif [ "$API_STATUS" = "401" ]; then
      bad "admin-bootstrap" "bootstrap key rejected — check it matches .env"
    else
      bad "admin-bootstrap" "HTTP $API_STATUS"
    fi
  fi
fi

# ── 3. Seed demo data (idempotent) ───────────────────────────────────────────
answer_id=""
if [ -z "${SKIP_SEED:-}" ]; then
  echo ""
  echo "[3/5] Demo exam / question / student / answer"

  # Teacher account: POST /api/exams etc. are Teacher-only by design.
  tlogin=$(api POST /api/auth/login "{\"email\":\"$TEACHER_EMAIL\",\"password\":\"$TEACHER_PASSWORD\"}")
  ttoken=$(jsonget "$tlogin" accessToken)
  if [ -z "$ttoken" ]; then
    [ -n "$token" ] && api POST /api/auth/users \
      "{\"email\":\"$TEACHER_EMAIL\",\"password\":\"$TEACHER_PASSWORD\",\"displayName\":\"Smoke Teacher\",\"role\":\"Teacher\"}" "$token" >/dev/null
    tlogin=$(api POST /api/auth/login "{\"email\":\"$TEACHER_EMAIL\",\"password\":\"$TEACHER_PASSWORD\"}")
    ttoken=$(jsonget "$tlogin" accessToken)
  fi
  refresh_status
  if [ -n "$ttoken" ]; then ok "teacher-account" "$TEACHER_EMAIL"
  else bad "teacher-account" "could not create or log in teacher — seeding skipped"; fi

  if [ -n "$ttoken" ]; then
    # Exam (reuse by name from a previous run).
    exams=$(api GET /api/exams "" "$ttoken")
    refresh_status
    exam_id=$(printf %s "$exams" | tr '{' '\n' | grep 'SMOKE-TEST Exam' | grep -o '"id":"[^"]*' | head -1 | cut -d'"' -f4)
    if [ -z "$exam_id" ]; then
      created=$(api POST /api/exams \
        '{"name":"SMOKE-TEST Exam","year":2026,"description":"Created by deploy/seed.sh - safe to delete"}' "$ttoken")
      refresh_status
      exam_id=$(jsonget "$created" id)
    fi
    [ -n "$exam_id" ] && ok "demo-exam" "id=${exam_id:0:8}" || bad "demo-exam" "HTTP $API_STATUS"

    # Question #1 within that exam.
    qs=$(api GET /api/questions/by-exam/"$exam_id" "" "$ttoken")
    refresh_status
    question_id=$(printf %s "$qs" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(next((q["id"] for q in d if q["number"]==1),""))' 2>/dev/null || true)
    if [ -z "$question_id" ]; then
      qbody=$(printf '{"examId":"%s","number":1,"text":"SMOKE TEST: Explain, in two or three sentences, how a central bank raising its policy rate typically affects consumer price inflation.","maxScore":10}' "$exam_id")
      created_q=$(api POST /api/questions "$qbody" "$ttoken")
      refresh_status
      question_id=$(jsonget "$created_q" id)
    fi
    [ -n "$question_id" ] && ok "demo-question" "maxScore=10" || bad "demo-question" "HTTP $API_STATUS"

    # Active rubric with criteria — REQUIRED for grading (grading uses saved
    # structured criteria only; no rubric file or text is sent at grading time).
    api POST "/api/questions/$question_id/rubrics" \
      '{"criteria":[{"criterionId":"R1","description":"Mentions higher borrowing costs reducing demand","maxScore":4},{"criterionId":"R2","description":"Links weaker demand to slower inflation","maxScore":4},{"criterionId":"R3","description":"Coherent economic reasoning","maxScore":2}]}' \
      "$ttoken" >/dev/null
    if [ "$API_STATUS" = "200" ] || [ "$API_STATUS" = "201" ]; then
      ok "demo-rubric" "3 criteria, 10 pts total"
    else
      bad "demo-rubric" "HTTP $API_STATUS (may already exist)"
    fi

    # Student.
    students=$(api GET /api/students "" "$ttoken")
    refresh_status
    student_id=$(printf %s "$students" | python3 -c 'import sys,json;d=json.load(sys.stdin);print(next((s["id"] for s in d if s["externalId"]=="smoke-stu-001"),""))' 2>/dev/null || true)
    if [ -z "$student_id" ]; then
      created_s=$(api POST /api/students '{"externalId":"smoke-stu-001","displayName":"Smoke Student"}' "$ttoken")
      refresh_status
      student_id=$(jsonget "$created_s" id)
    fi
    [ -n "$student_id" ] && ok "demo-student" "id=${student_id:0:8}" || bad "demo-student" "HTTP $API_STATUS"

    # Answer sheet PNG generated locally (no network fetch).
    png="$(mktemp -t econgrader-smoke-answer-XXXXXX).png"
    python3 - "$png" <<'PYEOF' 2>/dev/null || rm -f "$png"
import sys
from PIL import Image, ImageDraw
img = Image.new("RGB", (900, 400), "white")
d = ImageDraw.Draw(img)
lines = [
    "Q1: If the central bank raises the policy rate,",
    "borrowing becomes more expensive, so households and",
    "firms spend less. Lower demand cools the economy and",
    "inflation falls toward the target.",
]
y = 30
for ln in lines:
    d.text((20, y), ln, fill="black")
    y += 40
img.save(sys.argv[1], "PNG")
PYEOF
    if [ -f "$png" ]; then
      upload_status=$(curl -sS -m 60 -o /tmp/seed-upload.json -w '%{http_code}' \
        -X POST "$BASE_URL/api/answers/upload" \
        -H "Authorization: Bearer $ttoken" \
        -F "studentId=$student_id" -F "questionId=$question_id" \
        -F "file=@$png;type=image/png" 2>/dev/null || echo 000)
      if [ "$upload_status" = "200" ] || [ "$upload_status" = "201" ]; then
        answer_id=$(jsonget "$(cat /tmp/seed-upload.json)" id)
        ok "demo-answer-upload" "$(wc -c <"$png" | tr -d ' ') bytes"
      else
        bad "demo-answer-upload" "HTTP $upload_status"
      fi
      rm -f "$png" /tmp/seed-upload.json
    else
      bad "demo-answer-upload" "Pillow missing (pip install pillow) — cannot render sample sheet"
    fi
  fi
fi

# ── 4. ONE real grading call ────────────────────────────────────────────────
if [ -z "${SKIP_GRADING:-}" ]; then
  echo ""
  echo "[4/5] Real AI grading run (provider = whatever this stack deploys)"
  if [ -z "$answer_id" ]; then
    bad "grading-run" "no answer available (seed failed, or SKIP_SEED without a previous seed run)"
  else
    grader_token="${ttoken:-$token}"
    run=$(api POST /api/grading/run \
      "{\"answerId\":\"$answer_id\",\"temperature\":0,\"promptVersion\":\"default\",\"runs\":1}" \
      "$grader_token" 240)
    refresh_status
    # A failed provider call can still return a payload (the grading service
    # falls back to score 0 with isValid=false). Gating only on the score
    # produces a false PASS on a dead provider — require isValid too.
    verdict=$(printf %s "$run" | python3 -c '
import sys, json
try:
    r = json.load(sys.stdin)["runs"][0]
except Exception as e:
    print("PARSE-FAIL", e)
    sys.exit(0)
err = r.get("error") or ""
if isinstance(err, list):
    err = " | ".join(str(x) for x in err)
valid = r.get("isValid", r.get("is_valid"))
print("provider=%s score=%s valid=%s error=%s" % (r.get("provider"), r.get("aiScore"), valid, str(err)[:120]))
' 2>/dev/null)
    case "$verdict" in
      *valid=True*|*valid=true*)
        ok "grading-run" "${verdict#error=}";;
      *)
        bad "grading-run" "AI run not valid: ${verdict#error=}"
        bad "grading-run" "check provider account/credit, MODEL_PROVIDER and keys in .env";;
    esac
  fi
else
  echo ""
  echo "[4/5] Grading skipped (SKIP_GRADING)"
fi

# ── 5. Summary ───────────────────────────────────────────────────────────────
echo ""
echo "=== SUMMARY ==="
if [ "$FAIL" -gt 0 ]; then
  echo "RESULT: FAIL ($FAIL failing check(s), $PASS passing)"
  exit 1
fi
echo "RESULT: PASS ($PASS checks) - deployment is live and grading end-to-end"
exit 0

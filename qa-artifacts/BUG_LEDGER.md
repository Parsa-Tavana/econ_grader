# QA Bug Ledger — EconGrader E2E session 2026-08-29

| ID | Sev | Component | Title | Repro | Evidence | Status | Root cause | Fix commit |
|----|-----|-----------|-------|-------|----------|--------|------------|------------|
| BUG-001 | S2 | api/questions | Duplicate question number returns 500 INTERNAL_ERROR instead of friendly 409 | `POST /api/questions {examId, number:1}` twice in same exam → second call HTTP 500 | repro: [repro_bug001.sh](repro_bug001.sh) | **FIXED** | Unique(ExamId,Number) DB violation surfaces as unhandled DbUpdateException → generic 500 handler | 5aac63a9 |
| BUG-002 | S2 | api/questions rubric | Empty criteria array creates ACTIVE rubric v(n) totalMaxScore=0; poisons grading | `POST /api/questions/{id}/rubrics {"criteria":[]}` → 201, becomes active | transcript above (2.12) | **FIXED** | No min-item validation on CreateRubricRequest.Criteria | 5aac63a9 |
| BUG-003 | S3 | api/answers | Teacher score accepts values above question MaxScore (9999 on max-20) persisted; metrics poisoned | `PUT /api/answers/{id}/teacher-score {"score":9999}` → 200 | transcript above (2.14) | **FIXED** | Only lower bound validated (INVALID_SCORE <0); no upper bound vs question.MaxScore | 5aac63a9 |

## Verified-good (no bug)
- All 12 probed endpoints 401 unauthenticated; garbage/`alg=none`/tampered JWTs all 401.
- Legacy `X-User-Id` header ignored without/with JWT (purged per PRODUCTION_PLAN).
- Teacher→admin endpoints (users CRUD, audit): clean 403s.
- IDOR: second teacher gets 403 on owner's exam/question/answer/image/runs/grade + corrector attach.
- Run/review immutability: PUT/PATCH/DELETE on run & review-history → 405.
- Answer re-upload replaces by design (201), runs preserved (cascade-safe), exactly one row per pair.
- Grading-run count validation solid (0/-1/11/'two' → 400); unknown answer → 404.
- Duplicate student ExternalId → friendly 409.

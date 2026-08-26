#!/bin/bash
# BUG-001 repro: duplicate question number -> currently 500, expected 409
TOK=$(python -c "import json;print(json.load(open('teacher_login.json'))['accessToken'])")
EID=$(curl -s -X POST http://localhost:8080/api/exams -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d '{"name":"QA DupNum repro","year":2026}' | python -c "import sys,json;print(json.load(sys.stdin)['id'])")
curl -s -o /dev/null -w "first  #1 -> %{http_code}\n" -X POST http://localhost:8080/api/questions -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "{\"examId\":\"$EID\",\"number\":1,\"text\":\"first\",\"maxScore\":5}"
curl -s -w "\nsecond #1 -> %{http_code}\n" -X POST http://localhost:8080/api/questions -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "{\"examId\":\"$EID\",\"number\":1,\"text\":\"second\",\"maxScore\":5}"

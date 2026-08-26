"""Internal service-to-service auth.

The .NET API sends `X-Internal-Key` on every call to protected endpoints.
Policy (fail-closed):
  - ENVIRONMENT=production: the shared key MUST be configured and MUST match —
    otherwise 401. An unset key in production rejects everything rather than
    silently opening the service.
  - non-production (local bare-metal dev): an UNSET key disables the check so
    `uvicorn app.main:app` works without compose. A SET key is still enforced,
    so the composed deployment can be rehearsed locally too.

/health stays open in every mode (uptime checks + Docker healthcheck).
"""
from __future__ import annotations

import hmac

from fastapi import HTTPException, Security, status
from fastapi.security import APIKeyHeader

from .config import settings

_HEADER_NAME = "X-Internal-Key"
_scheme = APIKeyHeader(name=_HEADER_NAME, auto_error=False)


async def require_internal_key(x_internal_key: str | None = Security(_scheme)) -> None:
    expected = settings.GRADING_INTERNAL_KEY
    if not expected:
        if settings.ENVIRONMENT.lower() == "production":
            # Fail-closed: never serve grading unauthenticated in production.
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="GRADING_INTERNAL_KEY is not configured",
            )
        return  # dev convenience only

    if x_internal_key is None or not hmac.compare_digest(x_internal_key, expected):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid internal key")

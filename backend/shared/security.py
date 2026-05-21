import base64
import binascii
import hashlib
import hmac
import json
import os
import time
from dataclasses import dataclass

from fastapi import Header, HTTPException, status
from sqlalchemy.exc import SQLAlchemyError

from shared.connections.postgresql_connection import SessionLocal
from shared.models.user import User


@dataclass(frozen=True)
class AuthPrincipal:
    user_id: int
    username: str


def _get_secret() -> bytes:
    secret = os.getenv("AUTH_TOKEN_SECRET", "dev-auth-token-secret-change-me")
    return secret.encode("utf-8")


def _base64url_encode(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def _base64url_decode(value: str) -> bytes:
    padding = "=" * (-len(value) % 4)
    return base64.urlsafe_b64decode(value + padding)


def create_access_token(user_id: int, username: str, expires_in_seconds: int = 86400, token_version: int = 0) -> str:
    payload = {
        "sub": str(user_id),
        "username": username,
        "exp": int(time.time()) + expires_in_seconds,
        "tv": int(token_version),
    }
    payload_json = json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    encoded_payload = _base64url_encode(payload_json)
    signature = hmac.new(_get_secret(), encoded_payload.encode("ascii"), hashlib.sha256).digest()
    encoded_signature = _base64url_encode(signature)
    return f"{encoded_payload}.{encoded_signature}"


def decode_access_token(token: str) -> AuthPrincipal:
    try:
        encoded_payload, encoded_signature = token.split(".", 1)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid access token",
        ) from exc

    expected_signature = hmac.new(_get_secret(), encoded_payload.encode("ascii"), hashlib.sha256).digest()
    try:
        provided_signature = _base64url_decode(encoded_signature)
    except (binascii.Error, ValueError) as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid access token",
        ) from exc
    if not hmac.compare_digest(expected_signature, provided_signature):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid access token",
        )

    try:
        payload = json.loads(_base64url_decode(encoded_payload))
        expires_at = int(payload["exp"])
        user_id = int(payload["sub"])
        username = str(payload["username"])
        token_tv = int(payload.get("tv", 0))
    except (binascii.Error, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid access token",
        ) from exc

    if expires_at < int(time.time()):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Access token expired",
        )

    # Check token_version against DB for simple revocation/versioning
    try:
        db = SessionLocal()
        user = db.query(User).filter(User.user_id == user_id).first()
    except SQLAlchemyError:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="User database unavailable",
        )
    finally:
        try:
            db.close()
        except Exception:
            pass

    if user is None:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid access token",
        )

    db_tv = int(getattr(user, "token_version", 0))
    if db_tv != token_tv:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Access token revoked",
        )

    return AuthPrincipal(user_id=user_id, username=username)


def get_current_user(authorization: str | None = Header(default=None)) -> AuthPrincipal:
    if not authorization:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Missing Authorization header",
        )

    prefix = "Bearer "
    if not authorization.startswith(prefix):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid Authorization header",
        )

    return decode_access_token(authorization[len(prefix):].strip())

import os
import secrets

from fastapi import APIRouter, Depends, HTTPException, status
from google.auth.transport import requests as google_requests
from google.oauth2 import id_token as google_id_token
from sqlalchemy import or_
from sqlalchemy.orm import Session

from app.connections.postgresql_connection import get_db
from app.models.user import User
from app.schemas.user_schemas import AuthResponse, GoogleTokenIn
from shared.security import create_access_token, AuthPrincipal, get_current_user
from fastapi import Depends

router = APIRouter(prefix="/auth", tags=["auth"])

@router.post("/google", response_model=AuthResponse)
def login_with_google(payload: GoogleTokenIn, db: Session = Depends(get_db)):
    google_client_id = os.getenv("GOOGLE_CLIENT_ID") or os.getenv("AUTH_GOOGLE_ID")
    if not google_client_id:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail="Missing GOOGLE_CLIENT_ID or AUTH_GOOGLE_ID in backend environment",
        )

    try:
        info = google_id_token.verify_oauth2_token(
            payload.id_token,
            google_requests.Request(),
            google_client_id,
            clock_skew_in_seconds=10,
        )
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid Google id_token",
        ) from exc

    email = info.get("email")

    if not email:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Google token has no email",
        )

    user = db.query(User).filter(or_(User.email == email, User.username == email)).first()

    if not user:
        user = User(
            username=email,
            email=email,
            password=secrets.token_urlsafe(32)
        )
        db.add(user)
        db.commit()
        db.refresh(user)
    elif user.email != email:
        user.email = email
        db.commit()
        db.refresh(user)

    token_version = getattr(user, "token_version", 0)
    access_token = create_access_token(user.user_id, user.username, token_version=token_version)

    return {
        "user_id": user.user_id,
        "username": user.username,
        "email": user.email,
        "created_at": user.created_at,
        "access_token": access_token,
        "token_type": "bearer",
    }


@router.post("/revoke")
def revoke_tokens(current_user: AuthPrincipal = Depends(get_current_user), db: Session = Depends(get_db)):
    """Invalidate all existing tokens for the authenticated user by bumping token_version."""
    user = db.query(User).filter(User.user_id == current_user.user_id).first()
    if not user:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="User not found")

    user.token_version = (getattr(user, "token_version", 0) or 0) + 1
    db.add(user)
    db.commit()
    return {"message": "User tokens revoked"}
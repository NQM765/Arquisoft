import os
import secrets
import urllib.parse

import requests
from fastapi import APIRouter, Depends, HTTPException, Query, status
from fastapi.responses import JSONResponse
from google.auth.transport import requests as google_requests
from google.oauth2 import id_token as google_id_token
from sqlalchemy.orm import Session

from app.connections.postgresql_connection import get_db
from app.models.user import User
from app.schemas.user_schemas import (
    GoogleTokenIn,
    OAuthInitResponse,
    OAuthSessionResponse,
    UserCreate,
    UserLogin,
    UserResponse,
)
from shared.oauth_session_store import oauth_store

router = APIRouter(prefix="/auth", tags=["auth"])

@router.post("/google", response_model=UserResponse)
def login_with_google(payload: GoogleTokenIn, db: Session = Depends(get_db)):
    google_client_id = os.getenv("GOOGLE_CLIENT_ID")
    if not google_client_id:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail="Missing GOOGLE_CLIENT_ID in backend environment",
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
    name = info.get("name", "")    

    if not email:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Google token has no email",
        )

    user = db.query(User).filter(User.email == email).first()

    if not user:
        
        user = User(
            username=name,
            email=email,
            password=secrets.token_urlsafe(32)
        )
        db.add(user)
        db.commit()
        db.refresh(user)

    return user


@router.post("/oauth/init", response_model=OAuthInitResponse)
def init_oauth_flow():
    state = secrets.token_urlsafe(32)
    session_id = oauth_store.create_session(state)

    google_client_id = os.getenv("GOOGLE_CLIENT_ID")
    frontend_origin = os.getenv("FRONTEND_ORIGIN", "http://localhost:3000")

    if not google_client_id:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail="Missing GOOGLE_CLIENT_ID in backend environment",
        )

    redirect_uri = f"{frontend_origin}/api/auth/callback"
    params = urllib.parse.urlencode({
        "client_id": google_client_id,
        "redirect_uri": redirect_uri,
        "response_type": "code",
        "scope": "openid email profile",
        "state": state,
    })
    auth_url = f"https://accounts.google.com/o/oauth2/v2/auth?{params}"

    return OAuthInitResponse(
        session_id=session_id,
        state=state,
        auth_url=auth_url,
    )


@router.get("/oauth/poll/{session_id}", response_model=OAuthSessionResponse)
def poll_oauth_session(session_id: str):
    session = oauth_store.wait_for_completion(session_id, timeout_seconds=60)
    if not session:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Session not found or expired",
        )

    return OAuthSessionResponse(
        session_id=session.session_id,
        status=session.status,
        user_data=session.user_data,
    )


@router.post("/oauth/complete", status_code=status.HTTP_200_OK)
def complete_oauth_session(
    state: str = Query(...),
    code: str = Query(...),
    session_id: str = Query(None),
    db: Session = Depends(get_db),
):
    if session_id:
        session = oauth_store.get_session(session_id)
    else:
        session = oauth_store.get_session_by_state(state)

    if not session:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Session not found or expired",
        )

    if session.state != state:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="State mismatch",
        )

    google_client_id = os.getenv("GOOGLE_CLIENT_ID")
    frontend_origin = os.getenv("FRONTEND_ORIGIN", "http://localhost:3000")
    redirect_uri = f"{frontend_origin}/api/auth/callback"

    token_url = "https://oauth2.googleapis.com/token"
    token_data = {
        "client_id": google_client_id,
        "code": code,
        "grant_type": "authorization_code",
        "redirect_uri": redirect_uri,
    }

    token_response = requests.post(token_url, data=token_data)
    if token_response.status_code != 200:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Failed to exchange code for token",
        )

    tokens = token_response.json()
    id_token = tokens.get("id_token")

    if not id_token:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="No id_token in response",
        )

    try:
        info = google_id_token.verify_oauth2_token(
            id_token,
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
    name = info.get("name", "")

    if not email:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Google token has no email",
        )

    user = db.query(User).filter(User.email == email).first()
    if not user:
        user = User(
            username=name,
            email=email,
            password=secrets.token_urlsafe(32)
        )
        db.add(user)
        db.commit()
        db.refresh(user)

    oauth_store.complete_session(session_id, {
        "user_id": user.user_id,
        "username": user.username,
        "email": user.email,
    })

    return {"status": "completed"}
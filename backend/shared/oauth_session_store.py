import secrets
import threading
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Optional


@dataclass
class OAuthSession:
    session_id: str
    state: str
    status: str
    user_data: Optional[dict] = None
    created_at: datetime = field(default_factory=datetime.utcnow)


class OAuthSessionStore:
    def __init__(self, timeout_seconds: int = 60):
        self._sessions: dict[str, OAuthSession] = {}
        self._timeout = timeout_seconds

    def create_session(self, state: str) -> str:
        session_id = secrets.token_urlsafe(32)
        session = OAuthSession(
            session_id=session_id,
            state=state,
            status="pending",
        )
        self._sessions[session_id] = session
        return session_id

    def get_session(self, session_id: str) -> Optional[OAuthSession]:
        return self._sessions.get(session_id)

    def get_session_by_state(self, state: str) -> Optional[OAuthSession]:
        for session in self._sessions.values():
            if session.state == state:
                return session
        return None

    def complete_session(self, session_id: str, user_data: dict) -> bool:
        session = self._sessions.get(session_id)
        if not session:
            return False
        session.status = "completed"
        session.user_data = user_data
        return True

    def wait_for_completion(self, session_id: str, timeout_seconds: int = None) -> Optional[OAuthSession]:
        session = self._sessions.get(session_id)
        if not session:
            return None
        
        timeout = timeout_seconds or self._timeout
        start = datetime.utcnow()
        poll_interval = 0.5
        
        while (datetime.utcnow() - start).total_seconds() < timeout:
            if session.status != "pending":
                return session
            threading.Event().wait(poll_interval)
        return session

    def cleanup_old_sessions(self):
        now = datetime.utcnow()
        cutoff = now - timedelta(seconds=self._timeout * 2)
        expired = [
            sid for sid, sess in self._sessions.items()
            if sess.created_at < cutoff
        ]
        for sid in expired:
            self._sessions.pop(sid, None)


oauth_store = OAuthSessionStore(timeout_seconds=60)
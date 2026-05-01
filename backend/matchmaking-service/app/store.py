from datetime import datetime, timezone
from threading import Lock
from uuid import uuid4

from fastapi import HTTPException, status

from app.schemas import CreateMatchRequest
from shared.security import AuthPrincipal

_lock = Lock()
_matches: dict[str, dict] = {}


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _as_response(match: dict) -> dict:
    players = list(match["players"].values())
    players.sort(key=lambda p: 0 if p["role"] == "host" else 1)
    return {
        "matchId": match["matchId"],
        "status": match["status"],
        "gameMode": match["gameMode"],
        "region": match["region"],
        "maxPlayers": match["maxPlayers"],
        "hostUserId": match["hostUserId"],
        "players": players,
        "relay": match["relay"],
        "createdAtUtc": match["createdAtUtc"],
        "updatedAtUtc": match["updatedAtUtc"],
    }


def create_match(request: CreateMatchRequest, principal: AuthPrincipal) -> dict:
    with _lock:
        now = _now_iso()
        match = {
            "matchId": str(uuid4()),
            "status": "waiting",
            "gameMode": request.gameMode,
            "region": request.region,
            "maxPlayers": request.maxPlayers,
            "hostUserId": principal.user_id,
            "players": {
                principal.user_id: {
                    "userId": principal.user_id,
                    "username": principal.username,
                    "role": "host",
                }
            },
            "relay": {"relayJoinCode": request.relayJoinCode},
            "createdAtUtc": now,
            "updatedAtUtc": now,
        }
        _matches[match["matchId"]] = match
        response = _as_response(match)
        response["role"] = "host"
        return response


def join_match(match_id: str, principal: AuthPrincipal) -> dict:
    with _lock:
        match = _matches.get(match_id)
        if match is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
            )
        if match["status"] != "waiting":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"Match is not available to join (status={match['status']})",
            )
        if len(match["players"]) >= match["maxPlayers"]:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is full"
            )

        match["players"][principal.user_id] = {
            "userId": principal.user_id,
            "username": principal.username,
            "role": "client",
        }
        match["status"] = "starting"
        match["updatedAtUtc"] = _now_iso()

        response = _as_response(match)
        response["role"] = "client"
        return response


def get_match(match_id: str, principal: AuthPrincipal | None = None) -> dict:
    with _lock:
        match = _matches.get(match_id)
        if match is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
            )
        if principal is not None and principal.user_id not in match["players"]:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Player is not in this match",
            )
        return _as_response(match)


def leave_match(match_id: str, principal: AuthPrincipal) -> dict:
    with _lock:
        match = _matches.get(match_id)
        if match is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
            )
        if principal.user_id not in match["players"]:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Player is not in this match",
            )

        del match["players"][principal.user_id]
        match["status"] = "closed"
        match["updatedAtUtc"] = _now_iso()
        return _as_response(match)

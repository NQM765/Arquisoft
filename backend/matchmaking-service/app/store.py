import os
import logging
from datetime import datetime, timedelta
from uuid import uuid4

from fastapi import HTTPException, status
from sqlalchemy.orm import Session

from app.cache import cache_get_snapshot, cache_set_snapshot
from app.models import Match
from app.schemas import (
    ClaimHostRequest,
    CreateMatchRequest,
    MatchSnapshotRequest,
    ReportHostLostRequest,
)
from shared.connections.postgresql_connection import SessionLocal
from shared.security import AuthPrincipal


HOST_STALE_SECONDS = int(os.getenv("HOST_STALE_SECONDS", "20"))
logger = logging.getLogger(__name__)


def _now() -> datetime:
    return datetime.utcnow()


def _iso(value: datetime | None) -> str | None:
    return value.isoformat() + "Z" if value is not None else None


def _players(match: Match) -> list[dict]:
    return list(match.players or [])


def _player(match: Match, user_id: int) -> dict | None:
    for player in _players(match):
        if int(player.get("userId", 0)) == user_id:
            return player
    return None


def _set_players(match: Match, players: list[dict]) -> None:
    match.players = players


def _is_host_stale(match: Match) -> bool:
    if match.status == "migrating":
        return True
    if match.status == "closed":
        return False
    if match.host_heartbeat_at is None:
        return True
    return _now() - match.host_heartbeat_at > timedelta(seconds=HOST_STALE_SECONDS)


def _as_response(match: Match, role: str | None = None) -> dict:
    players = _players(match)

    response = {
        "matchId": match.match_id,
        "status": match.status,
        "gameMode": match.game_mode,
        "region": match.region,
        "maxPlayers": match.max_players,
        "hostUserId": match.host_user_id,
        "players": players,
        "relay": {"relayJoinCode": match.relay_join_code} if match.relay_join_code else None,
        "hostGeneration": match.host_generation,
        "hostHeartbeatAtUtc": _iso(match.host_heartbeat_at),
        "snapshotSequence": match.snapshot_sequence,
        "hasSnapshot": match.snapshot is not None,
        "createdAtUtc": _iso(match.created_at),
        "updatedAtUtc": _iso(match.updated_at),
    }
    if role is not None:
        response["role"] = role
    return response


def _as_migration_state(match: Match) -> dict:
    host_stale = _is_host_stale(match)
    return {
        **_as_response(match),
        "hostStale": host_stale,
        "migrationHostUserId": _migration_host_user_id(match) if host_stale else None,
        "snapshot": match.snapshot,
    }


def _get_match_locked(db: Session, match_id: str) -> Match:
    match = (
        db.query(Match)
        .filter(Match.match_id == match_id)
        .with_for_update()
        .first()
    )
    if match is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
        )
    return match


def _require_player(match: Match, principal: AuthPrincipal) -> dict:
    player = _player(match, principal.user_id)
    if player is None:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Player is not in this match",
        )
    return player


def _migration_host_user_id(match: Match) -> int | None:
    for player in _players(match):
        user_id = int(player.get("userId", 0))
        if user_id > 0 and user_id != match.host_user_id:
            return user_id
    return None


def _close_open_matches_for_host(db: Session, host_user_id: int, now: datetime) -> None:
    previous_matches = (
        db.query(Match)
        .filter(
            Match.host_user_id == host_user_id,
            Match.status.in_(("waiting", "active", "migrating")),
        )
        .all()
    )
    for previous in previous_matches:
        previous.status = "closed"
        previous.relay_join_code = None
        previous.updated_at = now
        db.add(previous)


def create_match(request: CreateMatchRequest, principal: AuthPrincipal) -> dict:
    now = _now()
    player = {
        "userId": principal.user_id,
        "username": principal.username,
        "role": "host",
    }

    with SessionLocal() as db:
        _close_open_matches_for_host(db, principal.user_id, now)
        match = Match(
            match_id=str(uuid4()),
            status="waiting",
            game_mode=request.gameMode,
            region=request.region,
            max_players=request.maxPlayers,
            host_user_id=principal.user_id,
            relay_join_code=request.relayJoinCode,
            players=[player],
            host_heartbeat_at=now,
            created_at=now,
            updated_at=now,
        )
        db.add(match)
        db.commit()
        db.refresh(match)
        return _as_response(match, role="host")


def get_next_available_match(principal: AuthPrincipal) -> dict | None:
    with SessionLocal() as db:
        rows = (
            db.query(Match)
            .filter(Match.status.in_(("waiting", "active", "migrating")))
            .order_by(Match.updated_at.desc(), Match.created_at.desc())
            .all()
        )
        for match in rows:
            existing = _player(match, principal.user_id)
            host_stale = _is_host_stale(match)
            if host_stale:
                if existing is not None:
                    return {
                        "matchId": match.match_id,
                        "relayJoinCode": match.relay_join_code or "",
                        "hostGeneration": match.host_generation,
                        "hostStale": True,
                        "alreadyJoined": True,
                    }
                continue

            if (
                existing is not None
                and existing.get("role") != "host"
                and match.relay_join_code
            ):
                return {
                    "matchId": match.match_id,
                    "relayJoinCode": match.relay_join_code,
                    "hostGeneration": match.host_generation,
                    "hostStale": False,
                    "alreadyJoined": True,
                }
            if existing is not None:
                continue
            if len(_players(match)) < match.max_players and match.relay_join_code:
                return {
                    "matchId": match.match_id,
                    "relayJoinCode": match.relay_join_code,
                    "hostGeneration": match.host_generation,
                    "hostStale": False,
                    "alreadyJoined": False,
                }
        return None


def join_next_available_match(principal: AuthPrincipal) -> dict | None:
    with SessionLocal() as db:
        rows = (
            db.query(Match)
            .filter(Match.status.in_(("waiting", "active")))
            .order_by(Match.updated_at.desc(), Match.created_at.desc())
            .with_for_update()
            .all()
        )

        for match in rows:
            if not match.relay_join_code or _is_host_stale(match):
                continue

            players = _players(match)
            existing = _player(match, principal.user_id)
            if existing is not None:
                if existing.get("role") == "host":
                    continue
                return _as_response(match, role=existing.get("role", "client"))

            if len(players) >= match.max_players:
                continue

            players.append(
                {
                    "userId": principal.user_id,
                    "username": principal.username,
                    "role": "client",
                }
            )
            _set_players(match, players)
            match.status = "active" if len(players) >= 2 else "waiting"
            match.updated_at = _now()
            db.add(match)
            db.commit()
            db.refresh(match)
            return _as_response(match, role="client")

        return None


def join_match(match_id: str, principal: AuthPrincipal) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        if match.status not in {"waiting", "active"}:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"Match is not available to join (status={match.status})",
            )
        if not match.relay_join_code:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="Match has no active relay session",
            )
        if _is_host_stale(match):
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="Match host is stale",
            )

        players = _players(match)
        existing = _player(match, principal.user_id)
        if existing is not None:
            return _as_response(match, role=existing.get("role", "client"))

        if len(players) >= match.max_players:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is full"
            )

        players.append(
            {
                "userId": principal.user_id,
                "username": principal.username,
                "role": "client",
            }
        )
        _set_players(match, players)
        match.status = "active" if len(players) >= 2 else "waiting"
        match.updated_at = _now()
        db.add(match)
        db.commit()
        db.refresh(match)
        return _as_response(match, role="client")


def get_match(match_id: str, principal: AuthPrincipal | None = None) -> dict:
    with SessionLocal() as db:
        match = db.query(Match).filter(Match.match_id == match_id).first()
        if match is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
            )
        if principal is not None:
            _require_player(match, principal)
        return _as_response(match)


def leave_match(match_id: str, principal: AuthPrincipal) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        _require_player(match, principal)

        players = [
            player
            for player in _players(match)
            if int(player.get("userId", 0)) != principal.user_id
        ]
        _set_players(match, players)

        if not players:
            match.status = "closed"
            match.relay_join_code = None
        elif principal.user_id == match.host_user_id:
            match.status = "migrating"
            match.relay_join_code = None
            match.host_heartbeat_at = None
        else:
            match.status = "active"

        match.updated_at = _now()
        db.add(match)
        db.commit()
        db.refresh(match)
        return _as_response(match)


def record_host_heartbeat(match_id: str, principal: AuthPrincipal) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        _require_player(match, principal)
        if principal.user_id != match.host_user_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Only the current host can heartbeat this match",
            )
        if match.status == "closed":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is closed"
            )
        if match.status == "migrating":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="Match is migrating to a new host",
            )

        now = _now()
        match.host_heartbeat_at = now
        match.updated_at = now
        if match.status == "migrating":
            match.status = "active"

        db.add(match)
        db.commit()
        db.refresh(match)
        return _as_response(match, role="host")


def report_host_lost(
    match_id: str, request: ReportHostLostRequest, principal: AuthPrincipal
) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        _require_player(match, principal)

        if match.status == "closed":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is closed"
            )
        if principal.user_id == match.host_user_id:
            return _as_migration_state(match)
        if request.hostGeneration < match.host_generation:
            return _as_migration_state(match)

        match.status = "migrating"
        match.relay_join_code = None
        match.host_heartbeat_at = None
        match.updated_at = _now()
        db.add(match)
        db.commit()
        db.refresh(match)
        logger.info(
            "Host lost reported for match %s by user %s. Migration host user=%s.",
            match.match_id,
            principal.user_id,
            _migration_host_user_id(match),
        )
        return _as_migration_state(match)


def save_match_snapshot(
    match_id: str, request: MatchSnapshotRequest, principal: AuthPrincipal
) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        _require_player(match, principal)
        if principal.user_id != match.host_user_id:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Only the current host can snapshot this match",
            )
        if match.status == "closed":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is closed"
            )
        if match.status == "migrating":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="Match is migrating to a new host",
            )

        match.snapshot_sequence = max(match.snapshot_sequence + 1, request.sequence)
        match.snapshot = request.snapshot
        match.host_heartbeat_at = _now()
        match.updated_at = match.host_heartbeat_at
        db.add(match)
        db.commit()
        db.refresh(match)
        # Cache-aside: populate Redis after PG commit (graceful on failure)
        cache_set_snapshot(match_id, match.snapshot, match.snapshot_sequence)
        return _as_response(match, role="host")


def claim_host(
    match_id: str, request: ClaimHostRequest, principal: AuthPrincipal
) -> dict:
    with SessionLocal() as db:
        match = _get_match_locked(db, match_id)
        _require_player(match, principal)
        if match.status == "closed":
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT, detail="Match is closed"
            )
        if principal.user_id == match.host_user_id and not _is_host_stale(match):
            return _as_response(match, role="host")
        if not _is_host_stale(match):
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="Current host is still healthy",
            )
        migration_host_user_id = _migration_host_user_id(match)
        if migration_host_user_id is None:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="No eligible migration host is available",
            )
        if principal.user_id != migration_host_user_id:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"Host migration is assigned to user {migration_host_user_id}",
            )
        if not request.relayJoinCode:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Missing relayJoinCode",
            )

        previous_host_user_id = match.host_user_id
        players = []
        for player in _players(match):
            user_id = int(player.get("userId", 0))
            if user_id == previous_host_user_id and user_id != principal.user_id:
                continue
            if user_id == principal.user_id:
                player["role"] = "host"
            else:
                player["role"] = "client"
            players.append(player)

        now = _now()
        match.players = players
        match.host_user_id = principal.user_id
        match.relay_join_code = request.relayJoinCode
        match.host_generation += 1
        match.host_heartbeat_at = now
        match.status = "active"
        match.updated_at = now
        db.add(match)
        db.commit()
        db.refresh(match)
        logger.info(
            "Host migration claimed for match %s by user %s. generation=%s.",
            match.match_id,
            principal.user_id,
            match.host_generation,
        )
        return _as_response(match, role="host")


def get_migration_state(match_id: str, principal: AuthPrincipal) -> dict:
    with SessionLocal() as db:
        match = db.query(Match).filter(Match.match_id == match_id).first()
        if match is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="Match not found"
            )
        _require_player(match, principal)

        # Cache-aside: try Redis first, fall back to PG on miss/error
        cached = cache_get_snapshot(match_id)
        cached_seq = cached.get("sequence", -1) if isinstance(cached, dict) else -1
        cached_snap = cached.get("snapshot") if isinstance(cached, dict) else None
        if cached_snap is not None and cached_seq >= (match.snapshot_sequence or 0):
            match.snapshot = cached_snap
            match.snapshot_sequence = cached_seq
        elif match.snapshot is not None:
            # Populate cache on miss (write behind)
            cache_set_snapshot(match_id, match.snapshot, match.snapshot_sequence)

        return _as_migration_state(match)

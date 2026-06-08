import json
import logging
import os
from typing import Any

import redis

logger = logging.getLogger(__name__)

REDIS_URL = os.getenv("REDIS_URL", "redis://redis:6379/0")
SNAPSHOT_TTL = int(os.getenv("REDIS_SNAPSHOT_TTL", "300"))

_redis_client: redis.Redis | None = None


def _get_client() -> redis.Redis:
    global _redis_client
    if _redis_client is None:
        _redis_client = redis.Redis.from_url(REDIS_URL, decode_responses=True)
    return _redis_client


def _snapshot_key(match_id: str) -> str:
    return f"match:{match_id}:snapshot"


def cache_set_snapshot(match_id: str, snapshot: dict[str, Any], sequence: int) -> bool:
    try:
        client = _get_client()
        data = json.dumps({"sequence": sequence, "snapshot": snapshot})
        client.setex(_snapshot_key(match_id), SNAPSHOT_TTL, data)
        return True
    except Exception as exc:
        logger.warning("[CACHE] Failed to cache snapshot for %s: %s", match_id, exc)
        return False


def cache_get_snapshot(match_id: str) -> dict[str, Any] | None:
    try:
        client = _get_client()
        data = client.get(_snapshot_key(match_id))
        if data is None:
            return None
        parsed: dict[str, Any] = json.loads(data)
        return parsed
    except Exception as exc:
        logger.warning("[CACHE] Failed to read cached snapshot for %s: %s", match_id, exc)
        return None


def cache_recent_matches(limit: int = 10) -> list[dict[str, Any]]:
    """Obtiene últimas partidas como fallback cuando CB está OPEN"""
    try:
        client = _get_client()
        key = "matchmaking:recent_matches"
        cached = client.lrange(key, 0, limit - 1)
        return [json.loads(m) for m in cached]
    except Exception as exc:
        logger.warning("[CACHE] Failed to read recent matches: %s", exc)
        return []


def add_to_recent_matches(match: dict[str, Any]) -> None:
    """Agrega partida al cache para usar como fallback"""
    try:
        client = _get_client()
        key = "matchmaking:recent_matches"
        client.lpush(key, json.dumps(match))
        client.ltrim(key, 0, 9)  # Mantener últimas 10
    except Exception as exc:
        logger.warning("[CACHE] Failed to add recent match: %s", exc)

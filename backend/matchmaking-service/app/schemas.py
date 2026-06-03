from typing import Any

from pydantic import BaseModel, Field


class CreateMatchRequest(BaseModel):
    gameMode: str = Field(default="standard", min_length=1, max_length=40)
    region: str = Field(default="default", min_length=1, max_length=40)
    maxPlayers: int = Field(default=2, ge=2, le=4)
    relayJoinCode: str  # enviado por Unity junto con los demás campos


class JoinMatchRequest(BaseModel):
    matchId: str


class MatchSnapshotRequest(BaseModel):
    sequence: int = Field(default=0, ge=0)
    snapshot: dict[str, Any]


class ClaimHostRequest(BaseModel):
    relayJoinCode: str = Field(min_length=1)


class ReportHostLostRequest(BaseModel):
    hostGeneration: int = Field(default=0, ge=0)


class RelaySessionData(BaseModel):
    relayJoinCode: str


class MatchPlayer(BaseModel):
    userId: int
    username: str
    role: str


class MatchResponse(BaseModel):
    matchId: str
    status: str
    gameMode: str
    region: str
    maxPlayers: int
    hostUserId: int
    players: list[MatchPlayer]
    relay: RelaySessionData | None = None
    hostGeneration: int = 0
    hostHeartbeatAtUtc: str | None = None
    snapshotSequence: int = 0
    hasSnapshot: bool = False
    createdAtUtc: str
    updatedAtUtc: str


class CreateMatchResponse(MatchResponse):
    role: str


class JoinMatchResponse(MatchResponse):
    role: str


class NextMatchResponse(BaseModel):
    matchId: str
    relayJoinCode: str
    hostGeneration: int = 0
    hostStale: bool = False
    alreadyJoined: bool = False


class MigrationStateResponse(MatchResponse):
    hostStale: bool
    migrationHostUserId: int | None = None
    snapshot: dict[str, Any] | None = None

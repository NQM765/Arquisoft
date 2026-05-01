from pydantic import BaseModel, Field


class CreateMatchRequest(BaseModel):
    gameMode: str = Field(default="standard", min_length=1, max_length=40)
    region: str = Field(default="default", min_length=1, max_length=40)
    maxPlayers: int = Field(default=2, ge=2, le=4)
    relayJoinCode: str  # enviado por Unity junto con los demás campos


class JoinMatchRequest(BaseModel):
    matchId: str


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
    createdAtUtc: str
    updatedAtUtc: str


class CreateMatchResponse(MatchResponse):
    role: str


class JoinMatchResponse(MatchResponse):
    role: str

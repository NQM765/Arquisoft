from fastapi import APIRouter, Depends, status

from app.rabbit import consume_next_available_match, publish_match_available
from app.schemas import (
    CreateMatchRequest,
    CreateMatchResponse,
    JoinMatchResponse,
    MatchResponse,
)
from app.store import create_match, get_match, join_match, leave_match
from shared.security import AuthPrincipal, get_current_user

router = APIRouter(prefix="/matchmaking", tags=["matchmaking"])


@router.post(
    "/matches", response_model=CreateMatchResponse, status_code=status.HTTP_201_CREATED
)
def create(
    request: CreateMatchRequest,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    """
    El host crea una partida. Unity envía un solo JSON con todos los campos:
    { gameMode, region, maxPlayers, relayJoinCode }
    """
    response = create_match(request, current_user)
    publish_match_available(response["matchId"], request.relayJoinCode)
    return response


@router.get("/queue/next")
def next_available_match(current_user: AuthPrincipal = Depends(get_current_user)):
    """
    El cliente consume el próximo match disponible de RabbitMQ.
    Devuelve { matchId, relayJoinCode } o null si no hay ninguno.
    """
    return consume_next_available_match()


@router.post("/matches/{match_id}/join", response_model=JoinMatchResponse)
def join(
    match_id: str,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    return join_match(match_id, current_user)


@router.get("/matches/{match_id}", response_model=MatchResponse)
def read_match(match_id: str, current_user: AuthPrincipal = Depends(get_current_user)):
    return get_match(match_id, current_user)


@router.post("/matches/{match_id}/leave", response_model=MatchResponse)
def leave(match_id: str, current_user: AuthPrincipal = Depends(get_current_user)):
    return leave_match(match_id, current_user)

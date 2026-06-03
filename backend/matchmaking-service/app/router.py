from fastapi import APIRouter, Depends, status

from app.schemas import (
    ClaimHostRequest,
    CreateMatchRequest,
    CreateMatchResponse,
    JoinMatchResponse,
    MatchResponse,
    MatchSnapshotRequest,
    MigrationStateResponse,
    NextMatchResponse,
    ReportHostLostRequest,
)
from app.store import (
    claim_host,
    create_match,
    get_match,
    get_migration_state,
    get_next_available_match,
    join_match,
    join_next_available_match,
    leave_match,
    record_host_heartbeat,
    report_host_lost,
    save_match_snapshot,
)
from shared.security import AuthPrincipal, get_current_user

router = APIRouter(prefix="/matchmaking", tags=["matchmaking"])


@router.post(
    "/matches", response_model=CreateMatchResponse, status_code=status.HTTP_201_CREATED
)
def create(
    request: CreateMatchRequest,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    return create_match(request, current_user)


@router.get("/queue/next", response_model=NextMatchResponse | None)
def next_available_match(current_user: AuthPrincipal = Depends(get_current_user)):
    return get_next_available_match(current_user)


@router.post("/queue/join-next", response_model=JoinMatchResponse | None)
def join_next_available(current_user: AuthPrincipal = Depends(get_current_user)):
    return join_next_available_match(current_user)


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


@router.post("/matches/{match_id}/heartbeat", response_model=MatchResponse)
def heartbeat(match_id: str, current_user: AuthPrincipal = Depends(get_current_user)):
    return record_host_heartbeat(match_id, current_user)


@router.post("/matches/{match_id}/snapshot", response_model=MatchResponse)
def snapshot(
    match_id: str,
    request: MatchSnapshotRequest,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    return save_match_snapshot(match_id, request, current_user)


@router.get("/matches/{match_id}/migration", response_model=MigrationStateResponse)
def migration_state(
    match_id: str, current_user: AuthPrincipal = Depends(get_current_user)
):
    return get_migration_state(match_id, current_user)


@router.post("/matches/{match_id}/migration/claim", response_model=CreateMatchResponse)
def claim_migration_host(
    match_id: str,
    request: ClaimHostRequest,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    return claim_host(match_id, request, current_user)


@router.post(
    "/matches/{match_id}/migration/report-host-lost",
    response_model=MigrationStateResponse,
)
def report_migration_host_lost(
    match_id: str,
    request: ReportHostLostRequest,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    return report_host_lost(match_id, request, current_user)

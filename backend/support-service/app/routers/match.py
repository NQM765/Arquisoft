from fastapi import APIRouter, Depends, status

from app.connections.firebase_connection import db
from app.schemas.match_schemas import ReceivedPayload
from shared.security import AuthPrincipal, get_current_user

router = APIRouter(tags=["match"])

@router.post("/support/session-summary", status_code=status.HTTP_201_CREATED)
@router.post("/match/session-summary", status_code=status.HTTP_201_CREATED)
def receive_match_summary(
    payload: ReceivedPayload,
    current_user: AuthPrincipal = Depends(get_current_user),
):
    payload_dict = payload.dict()
    payload_dict["userId"] = current_user.user_id
    payload_dict["username"] = current_user.username

    doc_ref = db.collection("match_summaries").add(payload_dict)
    doc_id = doc_ref[1].id

    return {"message": "Match summary received and stored successfully", "id": doc_id}
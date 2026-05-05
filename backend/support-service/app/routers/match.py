from fastapi import APIRouter, Depends, HTTPException, status

from app.connections.firebase_connection import db
from app.schemas.match_schemas import ReceivedPayload, PayloadinDB

router = APIRouter(prefix="/match", tags=["match"])

@router.post("/session-summary", status_code=status.HTTP_201_CREATED)
def receive_match_summary(payload: ReceivedPayload):
    if db is None:
        raise HTTPException(status_code=503, detail="Firebase not configured")
    
    payload_dict = payload.dict()

    doc_ref = db.collection("match_summaries").add(payload_dict)
    doc_id = doc_ref[1].id

    return {"message": "Match summary received and stored successfully", "id": doc_id}
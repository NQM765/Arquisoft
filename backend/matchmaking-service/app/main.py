import socket
import time

from fastapi import FastAPI
from sqlalchemy import inspect, text
from sqlalchemy.exc import OperationalError

from app.models import Match
from app.router import router as matchmaking_router
from shared.connections.postgresql_connection import Base, engine
from shared.cors import configure_cors

app = FastAPI(title="RTS Matchmaking API")

configure_cors(app)
app.include_router(matchmaking_router)
app.include_router(matchmaking_router, prefix="/api")


@app.get("/health")
def health():
    return {"status": "ok", "component": "matchmaking", "instance": socket.gethostname()}


@app.on_event("startup")
def on_startup():
    _ = Match
    for attempt in range(1, 11):
        try:
            Base.metadata.create_all(bind=engine)
            ensure_match_schema()
            return
        except OperationalError:
            if attempt == 10:
                raise
            time.sleep(2)


def ensure_match_schema():
    inspector = inspect(engine)
    if "matches" not in inspector.get_table_names():
        return

    existing_columns = {column["name"] for column in inspector.get_columns("matches")}
    statements = []
    if "snapshot" not in existing_columns:
        statements.append("ALTER TABLE matches ADD COLUMN snapshot JSON")
    if "snapshot_sequence" not in existing_columns:
        statements.append("ALTER TABLE matches ADD COLUMN snapshot_sequence INTEGER NOT NULL DEFAULT 0")
    if "host_generation" not in existing_columns:
        statements.append("ALTER TABLE matches ADD COLUMN host_generation INTEGER NOT NULL DEFAULT 0")
    if "host_heartbeat_at" not in existing_columns:
        statements.append("ALTER TABLE matches ADD COLUMN host_heartbeat_at TIMESTAMP")

    if not statements:
        return

    with engine.begin() as connection:
        for statement in statements:
            connection.execute(text(statement))

import os
import socket
import time

from dotenv import load_dotenv
from fastapi import FastAPI
from sqlalchemy import inspect, text
from sqlalchemy.exc import OperationalError
from app.connections.postgresql_connection import Base, engine
from app.routers.auth import router as auth_router
from app.routers.match import router as match_router
from shared.cors import configure_cors

from fastapi.middleware.httpsredirect import HTTPSRedirectMiddleware

app = FastAPI()


load_dotenv()
configure_cors(app)

app.include_router(auth_router)
app.include_router(match_router)

if os.getenv("ENABLE_HTTPS_REDIRECT", "false").lower() in {"1", "true", "yes"}:
    app.add_middleware(HTTPSRedirectMiddleware)

@app.get("/")
async def root():
    return {"message": "Https funcionando :D!"}

@app.get("/health")
def health():
    return {"status": "ok", "component": "support", "instance": socket.gethostname()}


def ensure_users_schema() -> None:
    inspector = inspect(engine)
    if not inspector.has_table("users"):
        return

    columns = {column["name"]: column for column in inspector.get_columns("users")}
    statements = []

    if "email" not in columns:
        statements.append("ALTER TABLE users ADD COLUMN email VARCHAR(255)")

    if "username" in columns and engine.dialect.name == "postgresql":
        statements.append("ALTER TABLE users ALTER COLUMN username TYPE VARCHAR(255)")

    if "token_version" not in columns:
        statements.append("ALTER TABLE users ADD COLUMN token_version INTEGER DEFAULT 0 NOT NULL")

    statements.append("CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email ON users (email)")

    with engine.begin() as connection:
        for statement in statements:
            connection.execute(text(statement))


@app.on_event("startup")
def on_startup():
    for attempt in range(1, 11):
        try:
            Base.metadata.create_all(bind=engine)
            ensure_users_schema()
            return
        except OperationalError:
            if attempt == 10:
                raise
            time.sleep(2)

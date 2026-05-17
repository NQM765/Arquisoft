from fastapi import FastAPI
from fastapi.middleware.httpsredirect import HTTPSRedirectMiddleware

from app.router import router as matchmaking_router
from shared.cors import configure_cors

app = FastAPI(title="RTS Matchmaking API")

configure_cors(app)
app.add_middleware(HTTPSRedirectMiddleware)
app.include_router(matchmaking_router)


@app.get("/health")
def health():
    return {"status": "ok", "component": "matchmaking"}

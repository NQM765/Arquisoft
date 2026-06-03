from datetime import datetime

from sqlalchemy import Column, DateTime, Integer, JSON, String

from shared.connections.postgresql_connection import Base


class Match(Base):
    __tablename__ = "matches"

    match_id = Column(String(64), primary_key=True)
    status = Column(String(32), nullable=False, default="waiting")
    game_mode = Column(String(40), nullable=False, default="standard")
    region = Column(String(40), nullable=False, default="default")
    max_players = Column(Integer, nullable=False, default=2)
    host_user_id = Column(Integer, nullable=False)
    relay_join_code = Column(String(128), nullable=True)
    players = Column(JSON, nullable=False, default=list)
    snapshot = Column(JSON, nullable=True)
    snapshot_sequence = Column(Integer, nullable=False, default=0)
    host_generation = Column(Integer, nullable=False, default=0)
    host_heartbeat_at = Column(DateTime, nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)
    updated_at = Column(DateTime, default=datetime.utcnow, nullable=False)

from datetime import datetime
from sqlalchemy import Column, Integer, String, DateTime
from app.connections.postgresql_connection import Base

class User(Base):
    __tablename__ = "users"
    
    user_id = Column(Integer, primary_key=True)
    username = Column(String(255), nullable=False)
    email = Column(String(255), unique=True, nullable=True)
    password = Column(String(255), nullable=False)
    created_at = Column(DateTime, default=datetime.utcnow)
    token_version = Column(Integer, default=0, nullable=False)

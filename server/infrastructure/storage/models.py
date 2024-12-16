# infrastructure/storage/models.py (NEW FILE)
from sqlalchemy import Column, String, Boolean, Integer, Float
from .db import Base

class DBUser(Base):
    __tablename__ = "users"
    username = Column(String, primary_key=True, index=True)
    password = Column(String)
    current_map = Column(String)
    current_zone = Column(String, default="Main")
    x = Column(Float, default=0.0)
    y = Column(Float, default=0.0)
    z = Column(Float, default=0.0)
    yaw = Column(Float, default=0.0)
    pitch = Column(Float, default=0.0)
    health = Column(Integer, default=10000)
    energy = Column(Integer, default=10000)
    logged_in = Column(Boolean, default=False)
    role = Column(String, default="player")
    # inventory can be handled as JSON if needed, omitted for brevity

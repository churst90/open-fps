# infrastructure/storage/database_user_repository.py
from typing import Optional
from sqlalchemy.orm import Session
from .db import SessionLocal, Base, engine
from domain.users.user import User
from .models import DBUser
import json
import bcrypt

# Create tables if not exist
Base.metadata.create_all(bind=engine)

class DatabaseUserRepository:
    def __init__(self):
        pass

    def _get_session(self) -> Session:
        return SessionLocal()

    async def create_account(self, event_data) -> bool:
        username = event_data["username"]
        password = event_data["password"]
        role = event_data["role"]
        current_map = event_data["current_map"]

        # Check if user exists
        async with self._get_session() as db:
            existing = db.query(DBUser).filter(DBUser.username==username).first()
            if existing:
                return False
            # Hash password
            hashed_pw = bcrypt.hashpw(password.encode('utf-8'), bcrypt.gensalt()).decode('utf-8')
            new_user = DBUser(
                username=username,
                password=hashed_pw,
                current_map=current_map,
                role=role,
                logged_in=False
            )
            db.add(new_user)
            db.commit()
            return True

    async def authenticate_user(self, username, password):
        async with self._get_session() as db:
            db_user = db.query(DBUser).filter(DBUser.username==username).first()
            if not db_user:
                return None
            if bcrypt.checkpw(password.encode('utf-8'), db_user.password.encode('utf-8')):
                db_user.logged_in = True
                db.commit()
                # Convert db_user to dict as user_data
                user_data = {
                    "username": db_user.username,
                    "password": db_user.password,
                    "logged_in": db_user.logged_in,
                    "current_map": db_user.current_map,
                    "role": db_user.role,
                    "position": (db_user.x, db_user.y, db_user.z),
                    "yaw": db_user.yaw,
                    "pitch": db_user.pitch,
                    "health": db_user.health,
                    "energy": db_user.energy,
                    "inventory": {}
                }
                return user_data
            return None

    async def deauthenticate_user(self, username):
        async with self._get_session() as db:
            db_user = db.query(DBUser).filter(DBUser.username==username).first()
            if not db_user:
                return False
            db_user.logged_in = False
            db.commit()
            return True

    async def load_user(self, username):
        async with self._get_session() as db:
            db_user = db.query(DBUser).filter(DBUser.username==username).first()
            if not db_user:
                return None
            # Create a User from DBUser
            user = User(
                username=db_user.username,
                password="", # hashed password stored in user
                current_map=db_user.current_map,
                current_zone=db_user.current_zone,
                position=(db_user.x, db_user.y, db_user.z),
                current_energy=db_user.energy,
                current_health=db_user.health,
                yaw=db_user.yaw,
                pitch=db_user.pitch
            )
            user.logged_in = db_user.logged_in
            user.role = db_user.role
            user._password = db_user.password
            return user

    async def save_user(self, user: User):
        async with self._get_session() as db:
            db_user = db.query(DBUser).filter(DBUser.username==user.username).first()
            if not db_user:
                return
            db_user.password = user.password
            db_user.current_map = user.current_map
            db_user.current_zone = user.current_zone
            db_user.x, db_user.y, db_user.z = user.position
            db_user.yaw = user.yaw
            db_user.pitch = user.pitch
            db_user.health = user.health
            db_user.energy = user.energy
            db_user.logged_in = user.logged_in
            db_user.role = user.role
            db.commit()

    async def save_all_users(self):
        # With a DB, all data is always persisted. Just pass here.
        pass

    async def get_all_usernames(self) -> list:
        async with self._get_session() as db:
            result = db.query(DBUser.username).all()
            return [r[0] for r in result]

    async def get_logged_in_usernames(self) -> list:
        async with self._get_session() as db:
            result = db.query(DBUser.username).filter(DBUser.logged_in==True).all()
            return [r[0] for r in result]

    async def get_users_in_map(self, map_name: str) -> list:
        async with self._get_session() as db:
            db_users = db.query(DBUser).filter(DBUser.current_map==map_name).all()
            users = []
            for db_user in db_users:
                u = User(
                    username=db_user.username,
                    password="",
                    current_map=db_user.current_map,
                    current_zone=db_user.current_zone,
                    position=(db_user.x, db_user.y, db_user.z),
                    current_energy=db_user.energy,
                    current_health=db_user.health,
                    yaw=db_user.yaw,
                    pitch=db_user.pitch
                )
                u._password = db_user.password
                u.logged_in = db_user.logged_in
                u.role = db_user.role
                users.append(u)
            return users

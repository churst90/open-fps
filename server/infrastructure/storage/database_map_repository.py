# infrastructure/storage/database_map_repository.py
import json
import asyncio
import logging
from typing import Optional
from sqlalchemy.orm import Session
from .db import SessionLocal, Base, engine
from domain.maps.map import Map
from .map_parser import MapParser
from sqlalchemy import Column, String, Text
from sqlalchemy.orm import declarative_base

Base.metadata.create_all(bind=engine)

class DBMap(Base):
    __tablename__ = "maps"
    map_name = Column(String, primary_key=True)
    map_data = Column(Text)  # store custom map format text

class DatabaseMapRepository:
    def __init__(self, logger=None):
        self.logger = logger or logging.getLogger("database_map_repository")

    def _get_session(self) -> Session:
        return SessionLocal()

    async def load_map(self, map_name):
        async with self._get_session() as db:
            db_map = db.query(DBMap).filter(DBMap.map_name==map_name).first()
            if not db_map:
                self.logger.warning(f"Map '{map_name}' not found in DB.")
                return None
            map_data = db_map.map_data
            try:
                parsed_map = MapParser.parse_custom_map_format_to_dict(map_data)
                map_instance = Map.from_dict(parsed_map)
                self.logger.info(f"Map '{map_name}' loaded from DB.")
                return map_instance
            except Exception as e:
                self.logger.exception(f"Failed to parse map '{map_name}' from DB: {e}")
                return None

    async def save_map(self, game_map: Map):
        map_dict = game_map.to_dict()
        custom_map_format = MapParser.convert_dict_to_custom_map_format(map_dict)
        async with self._get_session() as db:
            db_map = db.query(DBMap).filter(DBMap.map_name==game_map.map_name).first()
            if not db_map:
                db_map = DBMap(map_name=game_map.map_name, map_data=custom_map_format)
                db.add(db_map)
            else:
                db_map.map_data = custom_map_format
            db.commit()
            self.logger.info(f"Map '{game_map.map_name}' saved to DB.")
            return True

    async def remove_map(self, map_name):
        async with self._get_session() as db:
            db_map = db.query(DBMap).filter(DBMap.map_name==map_name).first()
            if db_map:
                db.delete(db_map)
                db.commit()
                self.logger.info(f"Map '{map_name}' removed from DB.")
                return True
            self.logger.warning(f"Map '{map_name}' does not exist in DB.")
            return False

    async def map_exists(self, map_name):
        async with self._get_session() as db:
            db_map = db.query(DBMap.map_name).filter(DBMap.map_name==map_name).first()
            return db_map is not None

    async def get_all_map_names(self) -> list:
        async with self._get_session() as db:
            result = db.query(DBMap.map_name).all()
            names = [r[0] for r in result]
            self.logger.info(f"Retrieved list of all map names from DB: {names}")
            return names

    async def save_all_maps(self):
        # Maps are always saved individually. If loaded from DB, they are already persisted.
        pass

# infrastructure/storage/file_map_repository.py
import json
import aiofiles
import asyncio
import logging
from pathlib import Path
from infrastructure.logging.custom_logger import get_logger
from domain.maps.map import Map
from .map_parser import MapParser

class FileMapRepository:
    def __init__(self, logger=None, maps_dir='maps_data'):
        self.logger = logger or get_logger("map_registry", debug_mode=True)
        self._maps = {}
        self._lock = asyncio.Lock()
        self._maps_path = Path(maps_dir)
        self._maps_path.mkdir(exist_ok=True)

    async def load_map(self, map_name):
        map_file = self._maps_path / f"{map_name}.map"
        if map_file.exists():
            async with aiofiles.open(map_file, "r") as f:
                map_data = await f.read()
            if not map_data.strip():
                self.logger.warning(f"Skipping empty map file: {map_file}")
                return None
            try:
                parsed_map = MapParser.parse_custom_map_format_to_dict(map_data)
                map_instance = Map.from_dict(parsed_map)
                self._maps[map_name] = map_instance
                self.logger.info(f"Map '{map_name}' loaded successfully.")
                return map_instance
            except Exception as e:
                self.logger.exception(f"Failed to load map '{map_name}': {e}")
                return None
        self.logger.warning(f"Map file '{map_file}' not found.")
        return None

    async def save_map(self, map_instance: Map):
        map_file = self._maps_path / f"{map_instance.map_name}.map"
        map_dict = map_instance.to_dict()
        custom_map_format = MapParser.convert_dict_to_custom_map_format(map_dict)
        try:
            async with aiofiles.open(map_file, "w") as f:
                await f.write(custom_map_format)
            self.logger.info(f"Map '{map_instance.map_name}' saved successfully.")
            return True
        except Exception as e:
            self.logger.exception(f"Failed to save map '{map_instance.map_name}': {e}")
            return False

    async def remove_map(self, map_name):
        async with self._lock:
            if map_name in self._maps:
                del self._maps[map_name]
            map_file = self._maps_path / f"{map_name}.map"
            if map_file.exists():
                map_file.unlink()
                self.logger.info(f"Map '{map_name}' removed successfully.")
                return True
            self.logger.warning(f"Map '{map_name}' does not exist.")
            return False

    async def map_exists(self, map_name):
        map_file = self._maps_path / f"{map_name}.map"
        return map_file.exists()

    async def get_all_map_names(self) -> list:
        try:
            map_names = [f.stem for f in self._maps_path.glob("*.map")]
            self.logger.info(f"Retrieved list of all map names: {map_names}")
            return map_names
        except Exception as e:
            self.logger.exception("Failed to retrieve map names.")
            return []

    async def save_all_maps(self):
        async with self._lock:
            for map_name in await self.get_all_map_names():
                map_instance = await self.load_map(map_name)
                if map_instance:
                    await self.save_map(map_instance)
            self.logger.info("All maps saved successfully.")

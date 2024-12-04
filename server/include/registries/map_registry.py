import json
import aiofiles
import asyncio
from pathlib import Path
from .map_parser import MapParser
from ..assets.map import Map
from include.custom_logger import get_logger

class MapRegistry:
    def __init__(self, logger=None):
        self.logger = logger or get_logger("map_registry", debug_mode=True)
        self._maps = {}  # Cache of map instances
        self._lock = asyncio.Lock()
        self._maps_path = Path("maps")

    @property
    def maps(self):
        """Return all map instances."""
        return self._maps

    async def add_tile(self, map_name, tile_data):
        """Add a tile to the specified map."""
        if map_name in self._maps:
            try:
                map_instance = self._maps[map_name]
                await map_instance.add_tile(tile_data)
                await self.save_map(map_name)  # Save the map after modifying
                self.logger.info(f"Tile added to map '{map_name}' successfully.")
                return True
            except Exception as e:
                self.logger.exception(f"Failed to add tile to map '{map_name}': {e}")
                return False
        else:
            self.logger.warning(f"Map '{map_name}' does not exist.")
        return False

    async def remove_tile(self, map_name, tile_key):
        """Remove a tile from the specified map."""
        if map_name in self._maps:
            try:
                map_instance = self._maps[map_name]
                success = await map_instance.remove_tile(tile_key)
                if success:
                    await self.save_map(map_name)  # Save the map after modifying
                    self.logger.info(f"Tile '{tile_key}' removed from map '{map_name}' successfully.")
                    return True
                else:
                    self.logger.warning(f"Failed to remove tile '{tile_key}' from map '{map_name}'.")
                    return False
            except Exception as e:
                self.logger.exception(f"Failed to remove tile from map '{map_name}': {e}")
                return False
        else:
            self.logger.warning(f"Map '{map_name}' does not exist.")
            return False

    def get_all_map_names(self):
        """Return a list of all map names currently loaded in memory."""
        try:
            map_names = list(self._maps.keys())
            self.logger.info(f"Retrieved list of all map names: {map_names}")
            return map_names
        except Exception as e:
            self.logger.exception("Failed to retrieve map names.")
            return []

    async def create_map(self, event_data):
        """Create a new map."""
        map_name = event_data.get("map_name")
        if not map_name:
            self.logger.warning("Map creation failed: Map name is missing.")
            return False

        if map_name in self._maps:
            self.logger.warning(f"Map creation failed: Map '{map_name}' already exists.")
            return False

        try:
            map_instance = Map.from_dict(event_data)
            self._maps[map_name] = map_instance
            await self.save_map(map_name)
            self.logger.info(f"Map '{map_name}' created successfully.")
            return True
        except Exception as e:
            self.logger.exception(f"Failed to create map '{map_name}': {e}")
            return False

    async def remove_map(self, map_name):
        """Remove a map."""
        async with self._lock:
            if map_name in self._maps:
                try:
                    del self._maps[map_name]
                    map_file = self._maps_path / f"{map_name}.map"
                    if map_file.exists():
                        await aiofiles.os.remove(map_file)
                    self.logger.info(f"Map '{map_name}' removed successfully.")
                    return True
                except Exception as e:
                    self.logger.exception(f"Failed to remove map '{map_name}': {e}")
                    return False
            self.logger.warning(f"Map '{map_name}' does not exist.")
            return False

    async def load_map(self, map_name):
        """Load a map from the file system using MapParser."""
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

    async def save_map(self, map_name):
        """Save a map to the file system using MapParser."""
        if map_name in self._maps:
            try:
                map_instance = self._maps[map_name]
                map_file = self._maps_path / f"{map_name}.map"
                map_dict = map_instance.to_dict()
                custom_map_format = MapParser.convert_dict_to_custom_map_format(map_dict)

                async with aiofiles.open(map_file, "w") as f:
                    await f.write(custom_map_format)
                self.logger.info(f"Map '{map_name}' saved successfully.")
            except Exception as e:
                self.logger.exception(f"Failed to save map '{map_name}': {e}")

    async def load_all_maps(self):
        """Load all maps from disk into memory using MapParser."""
        async with self._lock:
            try:
                self._maps_path.mkdir(exist_ok=True)
                map_files = list(self._maps_path.glob("*.map"))

                if not map_files:
                    self.logger.warning("No map files found.")
                    return

                for map_file in map_files:
                    async with aiofiles.open(map_file, "r") as f:
                        map_data = await f.read()

                        if not map_data.strip():
                            self.logger.warning(f"Skipping empty map file: {map_file}")
                            continue

                        try:
                            parsed_map = MapParser.parse_custom_map_format_to_dict(map_data)
                            map_name = map_file.stem
                            self._maps[map_name] = Map.from_dict(parsed_map)
                            self.logger.info(f"Loaded map '{map_name}' successfully.")
                        except Exception as e:
                            self.logger.exception(f"Failed to load map '{map_file}': {e}")
            except Exception as e:
                self.logger.exception("Error while loading all maps.")

    async def save_all_maps(self):
        """Save all maps currently in memory to disk."""
        async with self._lock:
            try:
                for map_name in self._maps:
                    await self.save_map(map_name)
                self.logger.info("All maps saved successfully.")
            except Exception as e:
                self.logger.exception("Error while saving all maps.")

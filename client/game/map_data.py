# game/map_data.py
import logging
from typing import Dict, Any, Tuple, Optional

class MapData:
    """
    Represents the local client-side copy of the map.
    Stores map size, start position, tiles, zones, and maybe weather or other attributes.
    The server sends updates for tiles and zones, which we apply here.
    """

    def __init__(self):
        self.logger = logging.getLogger("MapData")

        # Basic map attributes
        self.map_name: str = "Unknown"
        self.map_size: Tuple[int,int,int,int,int,int] = (0,0,0,0,0,0)
        self.start_position: Tuple[int,int,int] = (0,0,0)
        self.is_public: bool = True
        self.owners = []
        self.tiles: Dict[str, Dict[str, Any]] = {}
        self.zones: Dict[str, Dict[str, Any]] = {}

    def load_from_dict(self, data: Dict[str, Any]):
        """
        Load full map data from a dictionary, likely provided by server.
        data format expected to match server's map.to_dict():
        {
          "map_name": str,
          "map_size": (x1,x2,y1,y2,z1,z2),
          "start_position": (x,y,z),
          "is_public": bool,
          "owners": [...],
          "tiles": {tile_key: {...}, ...},
          "zones": {zone_key: {...}, ...}
        }
        """
        self.map_name = data.get("map_name", "Unknown")
        self.map_size = tuple(data.get("map_size", (0,0,0,0,0,0)))
        self.start_position = tuple(data.get("start_position", (0,0,0)))
        self.is_public = data.get("is_public", True)
        self.owners = data.get("owners", [])
        self.tiles = data.get("tiles", {})
        self.zones = data.get("zones", {})
        self.logger.debug(f"Loaded map data for {self.map_name}.")

    def update_tile(self, tile_key: str, tile_data: Dict[str, Any]):
        """
        Update or add a tile. If tile_key exists, update its data, else add new.
        tile_data should include tile_position and tile_type, etc.
        """
        self.tiles[tile_key] = tile_data
        self.logger.debug(f"Tile {tile_key} updated/added.")

    def remove_tile(self, tile_key: str):
        """
        Remove a tile by key if it exists.
        """
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            self.logger.debug(f"Tile {tile_key} removed.")

    def update_zone(self, zone_key: str, zone_data: Dict[str, Any]):
        """
        Update or add a zone similarly.
        """
        self.zones[zone_key] = zone_data
        self.logger.debug(f"Zone {zone_key} updated/added.")

    def remove_zone(self, zone_key: str):
        """
        Remove a zone by key if it exists.
        """
        if zone_key in self.zones:
            del self.zones[zone_key]
            self.logger.debug(f"Zone {zone_key} removed.")

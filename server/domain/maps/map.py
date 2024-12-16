from typing import Dict, Tuple
from .tile import Tile
from .zone import Zone
from domain.physics.map_physics import MapPhysics

class Map:
    def __init__(self, map_name="", map_size=(0.0,0.0,0.0,0.0,0.0,0.0), start_position=(), is_public=True):
        # Convert map_size and start_position to floats
        self.map_name = map_name
        self.map_size = tuple(float(v) for v in map_size)
        self.start_position = tuple(float(v) for v in start_position)
        self.is_public = is_public
        self.users = {}
        self.owners = []
        self.tiles: Dict[str, Tile] = {}
        self.zones: Dict[str, Zone] = {}
        self.physics = MapPhysics()

    async def add_tile(self, key: str, new_tile: Tile) -> bool:
        if key in self.tiles:
            return False
        self.tiles[key] = new_tile
        return True

    async def remove_tile(self, tile_key: str) -> bool:
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            return True
        return False

    async def add_zone(self, key: str, new_zone: Zone) -> bool:
        if key in self.zones:
            return False
        self.zones[key] = new_zone
        return True

    async def remove_zone(self, zone_key: str) -> bool:
        if zone_key in self.zones:
            del self.zones[zone_key]
            return True
        return False

    async def add_owner(self, owner_key: str) -> bool:
        if owner_key not in self.owners:
            self.owners.append(owner_key)
            return True
        return False

    async def remove_owner(self, owner_key: str) -> bool:
        if owner_key in self.owners:
            self.owners.remove(owner_key)
            return True
        return False

    async def join_map(self, user_key: str) -> bool:
        if user_key not in self.users:
            self.users[user_key] = {"position": self.start_position}
            return True
        return False

    def is_tile_within_bounds(self, tile_position: tuple) -> bool:
        x_min, x_max, y_min, y_max, z_min, z_max = self.map_size
        x1, x2, y1, y2, z1, z2 = tile_position
        return (x_min <= x1 <= x2 <= x_max and
                y_min <= y1 <= y2 <= y_max and
                z_min <= z1 <= z2 <= z_max)

    def is_zone_within_bounds(self, zone_position: tuple) -> bool:
        x_min, x_max, y_min, y_max, z_min, z_max = self.map_size
        X1, X2, Y1, Y2, Z1, Z2 = zone_position
        return (x_min <= X1 <= X2 <= x_max and
                y_min <= Y1 <= Y2 <= y_max and
                z_min <= Z1 <= Z2 <= z_max)

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "start_position": self.start_position,
            "is_public": self.is_public,
            "owners": self.owners,
            "tiles": {tile_key: tile.to_dict() for tile_key, tile in self.tiles.items()},
            "zones": {zone_key: zone.to_dict() for zone_key, zone in self.zones.items()},
            "physics": self.physics.to_dict()
        }

    @classmethod
    def from_dict(cls, data):
        instance = cls(
            map_name=data["map_name"],
            map_size=tuple(float(v) for v in data["map_size"]),
            start_position=tuple(float(v) for v in data["start_position"]),
            is_public=data.get("is_public", True)
        )
        instance.owners = data.get("owners", [])
        from .tile import Tile
        from .zone import Zone
        instance.tiles = {k: Tile.from_dict(v) for k, v in data.get("tiles", {}).items()}
        instance.zones = {k: Zone.from_dict(v) for k, v in data.get("zones", {}).items()}

        from domain.physics.map_physics import MapPhysics
        physics_data = data.get("physics", {})
        instance.physics = MapPhysics.from_dict(physics_data)

        return instance

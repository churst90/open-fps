from include.assets.tile import Tile
from include.assets.zone import Zone

class Map:
    def __init__(self):
        # Map features and attributes
        self._map_name = ""
        self._map_size = (0, 0, 0, 0, 0, 0)
        self._start_position = ()
        self._users = {}
        self._owners = []
        self._tiles = {}
        self._zones = {}
        self._items = {}
        self._ai = {}
        self._weather = {"type": "clear", "intensity": 0, "duration": 0}
        self._is_public = True

    # Property for map_name
    @property
    def map_name(self):
        return self._map_name

    @map_name.setter
    def map_name(self, value):
        if isinstance(value, str):
            self._map_name = value
        else:
            raise ValueError("Map name must be a string.")

    # Property for map_size
    @property
    def map_size(self):
        return self._map_size

    @map_size.setter
    def map_size(self, value):
        if isinstance(value, tuple):
            self._map_size = value
        else:
            raise ValueError("Map size must be a tuple.")

    # Property for start_position
    @property
    def start_position(self):
        return self._start_position

    @start_position.setter
    def start_position(self, value):
        if isinstance(value, tuple):
            self._start_position = value
        else:
            raise ValueError("Start position must be a tuple.")

    # Property for weather
    @property
    def weather(self):
        return self._weather

    @weather.setter
    def weather(self, value):
        if isinstance(value, dict) and {"type", "intensity", "duration"} <= set(value.keys()):
            self._weather = value
        else:
            raise ValueError("Weather must be a dictionary with keys: 'type', 'intensity', and 'duration'.")

    # Property for is_public
    @property
    def is_public(self):
        return self._is_public

    @is_public.setter
    def is_public(self, value):
        if isinstance(value, bool):
            self._is_public = value
        else:
            raise ValueError("is_public must be a boolean value.")

    # Property for owners
    @property
    def owners(self):
        return self._owners

    # Dictionary to and from methods
    @classmethod
    def from_dict(cls, data):
        map_instance = cls()
        map_instance.map_name = data["map_name"]
        map_instance.map_size = data["map_size"]
        map_instance.start_position = data["start_position"]
        map_instance._owners = data.get("owners", [])
        map_instance._tiles = {tile_key: Tile.from_dict(tile_data) for tile_key, tile_data in data.get("tiles", {}).items()}
        map_instance._zones = {zone_key: Zone.from_dict(zone_data) for zone_key, zone_data in data.get("zones", {}).items()}
        return map_instance

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "start_position": self.start_position,
            "owners": self.owners,
            "tiles": {tile_key: tile.to_dict() for tile_key, tile in self._tiles.items()},
            "zones": {zone_key: zone.to_dict() for zone_key, zone in self._zones.items()}
        }

    # Methods to add and remove tiles
    async def add_tile(self, new_tile_data):
        try:
            new_tile = Tile(new_tile_data["tile_position"], new_tile_data["tile_type"], new_tile_data["is_wall"])
            self._tiles[new_tile.tile_key] = new_tile
        except Exception as e:
            print(f"Error adding tile: {e}")
            return False

    async def remove_tile(self, tile_key):
        if tile_key in self._tiles:
            del self._tiles[tile_key]
            return True
        else:
            print("Tile not found.")
            return False

    # Methods to add and remove zones
    async def add_zone(self, new_zone_data):
        try:
            new_zone = Zone(
                new_zone_data["zone_label"], new_zone_data["zone_position"], new_zone_data["is_safe"], new_zone_data["is_hazard"]
            )
            self._zones[new_zone.zone_key] = new_zone
        except Exception as e:
            print(f"Error adding zone: {e}")
            return False

    async def remove_zone(self, zone_key):
        if zone_key in self._zones:
            del self._zones[zone_key]
        else:
            print("Zone not found.")
            return False

    # User management methods
    async def join_map(self, username, user_instance):
        self._users[username] = user_instance

    async def leave_map(self, username):
        if username in self._users:
            del self._users[username]
        else:
            print(f"User {username} not found on map {self.map_name}.")
            return False

    # Owner management methods
    async def add_owner(self, owner):
        if owner not in self._owners:
            self._owners.append(owner)
        else:
            print("Owner already exists.")
            return False

    async def remove_owner(self, owner):
        if owner in self._owners:
            self._owners.remove(owner)
        else:
            print("Owner not found.")
            return False

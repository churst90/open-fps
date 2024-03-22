from include.assets.tile import Tile
from include.assets.zone import Zone

class Map:
    def __init__(self):
        # map features and attributes
        self.map_name = ""
        self.map_size = (0, 0, 0, 0, 0, 0)
        self.start_position = ()
        self.users = {}
        self.owners = []
        self.tiles = {}
        self.zones = {}
        self.items = {}
        self.ai = {}
        self.weather = {"type": "clear", "intensity": 0, "duration": 0}
        self.is_public = True

    async def set_weather(self, new_weather):
        try:
            self.weather = new_weather
            return
        except:
            print("Couldn't set weather for current map")
            return False

    def set_start_position(self, position):
        try:
            self.start_position = position
            return
        except:
            print("Could not set start position")
            return False

    def set_is_public(self, status=None):
        if status is None:
            # Toggle the current state if no status is provided
            self.is_public = not self.is_public
        else:
            # Set the state to the provided status if it's a boolean
            if isinstance(status, bool):
                self.is_public = status
            else:
                raise ValueError("Status must be a boolean value.")

    def set_map_name(self, name):
        try:
            self.map_name = name
            return
        except:
            print("Couldn't set the map name")
            return False

    def set_map_size(self, size):
        try:
            self.map_size = size
            return
        except:
            print("could not set map size")
            return False

    @classmethod
    def from_dict(cls, data):
        map_instance = cls()
        map_instance.map_name = data['map_name']
        map_instance.map_size = data['map_size']
        map_instance.start_position = data['start_position']
        map_instance.owners = data['owners']
        map_instance.tiles = {tile_key: Tile.from_dict(tile_data) for tile_key, tile_data in data.get('tiles', {}).items()}
        map_instance.zones = {zone_key: Zone.from_dict(zone_data) for zone_key, zone_data in data.get('zones', {}).items()}
        return map_instance

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "start_position": self.start_position,
            "owners": self.owners,
            "tiles": {tile_key: tile.to_dict() for tile_key, tile in self.tiles.items()},
            "zones": {zone_key: zone.to_dict() for zone_key, zone in self.zones.items()}
        }

    def get_map_name(self):
        return self.map_name

    async def add_tile(self, new_tile_data):
        # Extract necessary data from event_data
        tile_position = new_tile_data['tile_position']
        tile_type = new_tile_data['tile_type']
        is_wall = new_tile_data['is_wall']

        # try to add the new  Tile
        try:
            new_tile = Tile(tile_position, tile_type, is_wall)

            # Add the new tile to the tiles dictionary using its unique key
            self.tiles[new_tile.tile_key] = new_tile
            print("tile added")
            return
        except Exception as e:
            print(f"The tile couldn't be added to the map: {e}")
            return False

    async def remove_tile(self, key):
        tile_key = key

        if tile_key in self.tiles:
            del self.tiles[tile_key]
            return
        else:
            print("Couldn't remove the tile from the map")
            return False

    async def add_zone(self, new_zone_data):
        zone_label = new_zone_data['zone_label']
        zone_position = new_zone_data['zone_position']
        is_safe = new_zone_data['is_safe']
        is_hazard = new_zone_data['is_hazard']

        try:
            new_zone = Zone(zone_label, zone_position, is_safe, is_hazard)
            self.zones[new_zone.zone_key] = new_zone
            return
        except Exception as e:
            print(f"Couldn't add the zone to the map: {e}")
            return False

    async def remove_zone(self, key):
        zone_key = key

        # Remove the zone if it exists
        if zone_key in self.zones:
            del self.zones[zone_key]
            return
        else:
            print("couldn't remove the zone from the map")
            return False

    async def join_map(self, new_username, new_user_instance):
        username = new_username
        user_instance = new_user_instance
        position = user_instance.get_position()

        # Add the user instance to the users dictionary
        self.users[username] = user_instance
        return

    async def leave_map(self, current_username):
        username = current_username

        # Remove the user instance from the players dictionary using the username key
        if username in self.users:
            del self.users[username]
            return
        else:
            print(f"User {username} not found on map {self.map_name}")
            return False

    async def add_owner(self, new_owner):
        if new_owner not in self.owners:
            self.owners.append(new_owner)
            return
        else:
            print("user already an owner")
            return False

    async def remove_owner(self, current_owner):
        if current_owner in self.owners:
            self.owners.pop(current_owner)
            return
        else:
            print("owner not in the owners list")
            return False

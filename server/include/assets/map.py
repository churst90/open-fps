class Map:
    def __init__(self):
        # map features and attributes
        self.map_name = ""
        self.map_size = ()
        self.start_position = ()
        self.users = {}
        self.owners = []
        self.tiles = {}
        self.zones = {}
        self.items = {}
        self.ai = {}
        self.weather = {"type": "clear", "intensity": 0, "duration": 0}
        self.is_public = True

    async def update_weather(self, new_weather):
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

    def is_within_single_bounds(self, x, y, z):
        min_x, max_x, min_y, max_y, min_z, max_z = self.map_size
        return min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z

    def is_within_range_bounds(self, min_x, max_x, min_y, max_y, min_z, max_z):
        map_min_x, map_max_x, map_min_y, map_max_y, map_min_z, map_max_z = self.map_size
        return map_min_x <= min_x <= max_x <= map_max_x and \
               map_min_y <= min_y <= max_y <= map_max_y and \
               map_min_z <= min_z <= max_z <= map_max_z

    @classmethod
    def from_dict(cls, data):
        map_instance = cls()
        map_instance.map_name = data['map_name']
        map_instance.map_size = data['map_size']
        map_instance.start_position = data.get('start_position')
        map_instance.owners = data.get('owners', [])
        map_instance.tiles = data.get('tiles', {})
        map_instance.zones = data.get('zones', {})
        return map_instance

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "start_position": self.start_position,
            "owners": self.owners,
            "tiles": self.tiles,
            "zones": self.zones,
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
            self.tiles[new_tile.tile_key] = new_tile.to_dict()
            print("tile added")
            return
        except:
            print("The tile couldn't be added to the map")
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
        zone_type = new_zone_data['zone_type']

        try:
            new_zone = Zone(zone_label, zone_position, zone_type)
            self.zones[new_zone.zone_key] = new_zone.to_dict()
            return
        except:
            print("Couldn't add the zone to the map")
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

        # check the user's current position in comparison to map boundaries
        if position in self.is_within_single_bounds():
            # Add the user instance to the users dictionary
            self.users[username] = user_instance
            return
        else:
            print("User couldn't be added to the map")
            return False

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

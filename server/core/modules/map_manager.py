# standard imports
import random
from pathlib import Path
import json
import asyncio
from asyncio import Lock
import aiofiles  # Import aiofiles for async file operations

# project specific imports
from core.modules.tile import Tile
from core.modules.zone import Zone

class MapRegistry:
    def __init__(self, event_dispatcher):
        self.lock = Lock()
        self.instances = {}  # For storing Map instance objects for runtime use
        self.event_dispatcher = event_dispatcher
        self.maps_path = Path('maps')  # Define the maps directory path

    async def assign_user_to_map(self, username, map_name):
        await self.event_dispatcher.update_user_map(username, map_name, action="add")

    async def remove_user_from_map(self, username, map_name):
        await self.event_dispatcher.update_user_map(username, map_name, action="remove")

    async def create_map(self, data):
        # Extract the values from the incoming data
        username = data.get('username')
        map_name = data.get('map_name')
        map_size = data.get('map_size')
        start_position = data.get('start_position')

        # check to see if the map being created already exists
        if map_name in self.instances:

           # Dispatch an error back to the user
            await self.dispatcher.dispatch("handle_create_map", {
                "message_type": "create_map_failed",
                "username": username
            },
            scope = "private", recipient = username)
            return

        # Create a new Map instance using the provided event dispatcher
        new_map = Map(self.event_dispatcher, self)
        new_map.set_map_name(map_name)
        new_map.set_map_size(map_size)
        new_map.set_start_position(start_position)
        await new_map.add_owner(username)
        print(f"Default {new_map.map_name} map created successfully with dimensions {new_map.map_size}")

        # add the map instances to the instances dictionary
        self.instances[map_name] = new_map
        await self.event_dispatcher.dispatch("global_chat", {
            "message": f"Server: {map_name} map was just created by {username}."
        })
        try:
            await self.save_map(map_name)
        except Exception as e:
            print(f"Error saving map: {e}")

    async def get_map_instance(self, name):
        async with self.lock:
            map_instance = self.instances[name]
            return map_instance

    async def get_all_maps(self):
#        async with self.lock:
        return self.instances

    async def save_map(self, name):
        if name not in self.instances:
            print(f"Map '{name}' does not exist.")
            return

        map_instance = self.instances[name]
        map_data = map_instance.to_dict()
        self.maps_path.mkdir(parents=True, exist_ok=True)
        try:
            async with aiofiles.open(self.maps_path / f'{name}.map', 'w') as file:
                await file.write(json.dumps(map_data))
            print(f"Map '{name}' saved successfully.")
        except Exception as e:
            print(f"Failed to save map {name}: {e}")

    async def save_all_maps(self):
        async with self.lock:
            for map_name in self.instances.keys():  # Iterating through keys of self.instances
                await self.save_map(map_name)  # map_name is a string

    async def load_maps(self):
        async with self.lock:
            if not self.maps_path.exists():
                self.maps_path.mkdir(parents=True, exist_ok=True)
                print("No maps directory found. Created 'maps' directory.")
                # initial map data
                data = {'username': 'admin', "map_name": "Main", "map_size": (0, 10, 0, 10, 0, 10), "start_position": (0, 0, 0)}
                await self.create_map(data)
                await self.instances["Main"].setup_subscriptions()
                tile_data = {
                    "username": "admin",
                    "map_name": "Main",
                    "tile_position": (0, 10, 0, 10, 0, 0),
                    "tile_type": "grass",
                    "is_wall": False  # Example property for tile
                    }

                zone_data = {
                    "username": "admin",
                    "map_name": "Main",
                    "zone_label": "Main area",
                    "zone_position": (0, 10, 0, 10, 0, 0),
                    "zone_type": "grass"
                    }

                await self.event_dispatcher.dispatch('handle_add_tile', tile_data)
                await self.event_dispatcher.dispatch('handle_add_zone', zone_data)

                await self.save_map("Main")
                return
            for map_file in self.maps_path.glob('*.map'):
                map_name = map_file.stem
                async with aiofiles.open(map_file, 'r') as file:
                    map_data = json.loads(await file.read())
                self.instances[map_name] = Map.from_dict(map_data, self.event_dispatcher)
                print(f"Map '{map_name}' loaded successfully.")
                await self.instances[map_name].setup_subscriptions()
                print(f"Subscriptions added for '{map_name}")

class Map:
    def __init__(self, event_dispatcher, map_registry):
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

        # Required components
        self.event_dispatcher = event_dispatcher
        self.map_registry = map_registry

    async def setup_subscriptions(self):
        self.event_dispatcher.subscribe_internal("add_tile", self.add_tile)
        self.event_dispatcher.subscribe_internal("remove_tile", self.remove_tile)
        self.event_dispatcher.subscribe_internal("add_zone", self.add_zone)
        self.event_dispatcher.subscribe_internal("remove_zone", self.remove_zone)
        self.event_dispatcher.subscribe_internal("join_map", self.join_map)
        self.event_dispatcher.subscribe_internal("leave_map", self.leave_map)

    async def update_weather(self, new_weather):
        self.weather = new_weather
        self.event_dispatcher.dispatch('weather_change', {'weather': self.weather}, scope = "map", map_id = self.map_name)

    def set_start_position(self, position):
        self.start_position = position

    def set_is_public(self):
        if self.is_public:
            self.is_public = False
        else:
            self.is_public = True

    def set_map_name(self, name):
        self.map_name = name

    def set_map_size(self, size):
        self.map_size = size

    def is_within_single_bounds(self, x, y, z):
        min_x, max_x, min_y, max_y, min_z, max_z = self.map_size
        return min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z

    def is_within_range_bounds(self, min_x, max_x, min_y, max_y, min_z, max_z):
        map_min_x, map_max_x, map_min_y, map_max_y, map_min_z, map_max_z = self.map_size
        return map_min_x <= min_x <= max_x <= map_max_x and \
               map_min_y <= min_y <= max_y <= map_max_y and \
               map_min_z <= min_z <= max_z <= map_max_z

    @classmethod
    def from_dict(cls, data, event_dispatcher):
        map_instance = cls(event_dispatcher, MapRegistry)
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

    async def add_tile(self, event_data):
        # Extract necessary data from event_data
        tile_position = event_data.get('tile_position')
        tile_type = event_data.get('tile_type')
        is_wall = event_data.get('is_wall')

        # Create a new Tile instance
        new_tile = Tile(tile_position, tile_type, is_wall)
        
        # Add the new tile to the tiles dictionary using its unique key
        self.tiles[new_tile.tile_key] = new_tile.to_dict()
        print("tile added")

    async def remove_tile(self, event_data):
        tile_key = event_data['tile_key']
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            print(f"Tile removed from {self.map_name}: {tile_key}")

    async def add_zone(self, event_data):
        if event_data:
            zone_label = event_data['zone_label']
            zone_position = event_data.get('zone_position')
            zone_type = event_data.get('zone_type')
            new_zone = Zone(zone_label, zone_position, zone_type)
            self.zones[new_zone.zone_key] = new_zone.to_dict()
            print("zone added")
        else:
            print(f"Invalid zone type: {zone_type}")

    async def remove_zone(self, event_data):
        zone_key = event_data['zone_key']
        # Remove the zone if it exists
        if zone_key in self.zones:
            del self.zones[zone_key]
            print(f"Zone {zone_key} removed from {self.map_name}.")
        else:
            print(f"Zone {zone_key} not found in {self.map_name}.")

    async def join_map(self, event_data):
        username = event_data.get('username')
        user_instance = event_data.get('user_instance')
        # check the user's current position in comparison to map boundaries
        if user_instance.get_position() in self.is_within_single_bounds():
            # Add the user instance to the players dictionary under the username key
            self.users[username] = user_instance
            print(f"User {username} added to map {self.map_name}")

        # Update the event dispatcher user to map record
        await self.map_registry.assign_user_to_map(username, self.map_name)

    async def leave_map(self, event_data):
        username = event_data.get('username')

        # Remove the user instance from the players dictionary using the username key
        if username in self.users:
            del self.users[username]

            await self.map_registry.remove_user_to_map(username, self.map_name)
        else:
            print(f"User {username} not found on map {self.map_name}")

    async def add_owner(self, owner_name):
        if owner_name not in self.owners:
            self.owners.append(owner_name)
        else:
            print("user already an owner")

    async def remove_owner(self, owner_name):
        if owner_name in self.owners:
            self.owners.pop(owner_name)
        else:
            print("owner not in the owners list")

    async def add_item(self, event_data):
        item_key = event_data.get('item_key')
        item_data = event_data
        self.items[item_key] = item_data

    async def remove_item(self, event_data):
        item_key = event_data.get('item_key')
        del self.items[item_key]
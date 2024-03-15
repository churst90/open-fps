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
#        self.map_handler = None

    async def assign_user_to_map(self, username, map_name):
        await self.event_dispatcher.update_user_map(username, map_name, action="add")

    async def remove_user_from_map(self, username, map_name):
        await self.event_dispatcher.update_user_map(username, map_name, action="remove")

#    def set_handler(self, handler):
#        self.map_handler = handler

    async def create_map(self, name, size):
        if name in self.instances:
            print("Map already exists")
            raise ValueError(f"A map with the name '{name}' already exists.")
        # Create a new Map instance using the provided event dispatcher
        new_map = Map(self.event_dispatcher, self)
        new_map.set_map_name(name)
        new_map.set_map_size(size)
        print(f"Default {new_map.map_name} map created successfully with dimensions {new_map.map_size}")
        # Create the new map instance
        self.instances[name] = new_map
        self.event_dispatcher.dispatch("chat", {"message": f"Server: {new_map} map was just created."})
        try:
            await self.save_map(new_map.get_map_name())
        except Exception as e:
            print(f"Error saving map: {e}")

    async def get_map_instance(self, name):
#        async with self.lock:
        return (self.instances[name], "Error: Map not found.")

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
        for map_name in self.instances.keys():  # Iterating through keys of self.instances
            await self.save_map(map_name)  # map_name is a string

    async def load_maps(self):
        async with self.lock:
            if not self.maps_path.exists():
                self.maps_path.mkdir(parents=True, exist_ok=True)
                print("No maps directory found. Created 'maps' directory.")
                await self.create_map("Main", (0, 10, 0, 10, 0, 10))
                self.instances["Main"].setup_event_subscriptions()
                print(f"Subscriptions added for Main")
                tile_data = {
                    "map_name": "Main",
                    "tile_position": (0, 10, 0, 10, 0, 0),
                    "tile_type": "grass",
                    "is_wall": False  # Example property for tile
                    }

                zone_data = {
                    "map_name": "Main",
                    "zone_label": "Main area",
                    "zone_position": (0, 10, 0, 10, 0, 0),
                    "zone_type": "grass"
                    }

                await self.event_dispatcher.dispatch('add_tile', tile_data)
                await self.event_dispatcher.dispatch('add_zone', zone_data)

                await self.save_map("Main")
                return
            for map_file in self.maps_path.glob('*.map'):
                map_name = map_file.stem
                async with aiofiles.open(map_file, 'r') as file:
                    map_data = json.loads(await file.read())
                self.instances[map_name] = Map.from_dict(map_data, self.event_dispatcher)
                print(f"Map '{map_name}' loaded successfully.")
                self.instances[map_name].setup_event_subscriptions()
                print(f"Subscriptions added for '{map_name}")

class Map:
    def __init__(self, event_dispatcher, map_registry):
        self.map_name = ""
        self.map_size = ()
        self.users = {}
        self.tiles = {}
        self.zones = {}
        self.items = {}
        self.ai = {}
        self.event_dispatcher = event_dispatcher
        self.event_listeners = {
            'change': []
        }
        self.changed_elements = {"tiles": set(), "zones": set(), "items": set(), "ai": set()}
        self.weather = {"type": "clear", "intensity": 0, "duration": 0}
        self.map_registry = map_registry

    def setup_event_subscriptions(self):
        self.event_dispatcher.subscribe_internal('add_tile', self.add_tile)
        self.event_dispatcher.subscribe_internal('remove_tile', self.map_name)
        self.event_dispatcher.subscribe_internal('add_zone', self.add_zone)
        self.event_dispatcher.subscribe_internal('remove_zone', self.map_name)
        self.event_dispatcher.subscribe_internal('add_user', self.add_user)
        self.event_dispatcher.subscribe_internal('remove_user', self.map_name)

    def update_weather(self, new_weather):
        self.weather = new_weather
#        self.event_dispatcher.dispatch('weather_change', {'map': self.map_name, 'weather': self.weather})

    def on(self, event_name, listener):
        if event_name not in self.event_listeners:
            self.event_listeners[event_name] = []
        self.event_listeners[event_name].append(listener)

    def emit(self, event_name, data):
        for listener in self.event_listeners.get(event_name, []):
            listener(data)

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
        map_instance.tiles = data.get('tiles', {})
        map_instance.zones = data.get('zones', {})
        return map_instance

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "tiles": self.tiles,
            "zones": self.zones,
        }

    def get_map_name(self):
        return self.map_name

    def add_tile(self, event_data):
        # Extract necessary data from event_data
        tile_position = event_data.get('tile_position')
        tile_type = event_data.get('tile_type')
        is_wall = event_data.get('is_wall')

        # Create a new Tile instance
        new_tile = Tile(tile_position, tile_type, is_wall)
        
        # Add the new tile to the tiles dictionary using its unique key
        self.tiles[new_tile.tile_key] = new_tile.to_dict()
        print("tile added")

    def remove_tile(self, event_data):
        tile_key = event_data['tile_key']
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            print(f"Tile removed from {self.map_name}: {tile_key}")

    def add_zone(self, event_data):
        if event_data:
            zone_label = event_data['zone_label']
            zone_position = event_data.get('zone_position')
            zone_type = event_data.get('zone_type')
            new_zone = Zone(zone_label, zone_position, zone_type)
            self.zones[new_zone.zone_key] = new_zone.to_dict()
            print("zone added")
        else:
            print(f"Invalid zone type: {zone_type}")

    def remove_zone(self, event_data):
        zone_key = event_data['zone_key']
        # Remove the zone if it exists
        if zone_key in self.zones:
            del self.zones[zone_key]
            print(f"Zone {zone_key} removed from {self.map_name}.")
        else:
            print(f"Zone {zone_key} not found in {self.map_name}.")

    def add_user(self, event_data):
        username = event_data.get('username')
        user_instance = event_data.get('user_instance')
        # Add the user instance to the players dictionary under the username key
        self.users[username] = user_instance
        print(f"User {username} added to map {self.map_name}")
        self.assign_user_to_map(username, self.map_name)
        print("user added to the user map pairing dictionary in the event dispatcher")

    def remove_user(self, event_data):
        username = event_data.get('username')
        # Remove the user instance from the players dictionary using the username key
        if username in self.users:
            del self.users[username]
            print(f"User {username} removed from map {self.map_name}")
            self.remove_user_to_map(username, self.map_name)
            print("user removed from the user map pairing dictionary in the event dispatcher")
        else:
            print(f"User {username} not found on map {self.map_name}")

    async def add_item(self, event_data):
        item_key = event_data.get('item_key')
        item_data = event_data
        self.items[item_key] = item_data

    async def remove_item(self, event_data):
        item_key = event_data.get('item_key')
        del self.items[item_key]
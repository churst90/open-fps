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

    async def create_map(self, event_data):
        # Extract the values from the incoming event_data
        username = event_data['username']
        map_name = event_data['map_name']
        map_size = event_data['map_size']
        start_position = event_data['start_position']

        # check to see if the map being created already exists
        if map_name in self.instances:
           # Dispatch an error back to the user
            await self.dispatcher.dispatch("map_create_failed", {
                "message_type": "create_map_failed",
                "username": username,
                "message": "That map name already exists, please try again"
            })
            return

        # Create a new Map instance using the provided event dispatcher
        new_map = Map(self.event_dispatcher, self)
        new_map.set_map_name(map_name)
        new_map.set_map_size(map_size)
        new_map.set_start_position(start_position)
        await new_map.add_owner(username)

        print(f"Default {new_map.map_name} map created successfully with dimensions {new_map.map_size}")

        # add the map instance to the instances dictionary
        self.instances[map_name] = new_map
        await self.instances[map_name].setup_subscriptions()

        try:
            await self.save_map(map_name)
        except Exception as e:
            print(f"Error saving map: {e}")

        await self.event_dispatcher.dispatch("global_chat", {
            "username": "server",
            "message": f"{map_name} map was just created by {username}."
        })

    async def get_map_instance(self, name):
        map_instance = self.instances[name]
        return map_instance

    async def get_all_maps(self):
#        async with self.lock:
        return self.instances

    async def load_map(self, map_name):
        map_file_path = self.maps_path / f"{map_name}.map"
        
        # Check if the map file exists
        if not map_file_path.exists():
            print(f"Map file for '{map_name}' does not exist.")
            return None
        
        # Read the map file
        try:
            async with aiofiles.open(map_file_path, 'r') as file:
                map_data = json.loads(await file.read())
                
                # Instantiate the Map object from the map data
                map_instance = Map.from_dict(map_data, self.event_dispatcher)
                
                # Optionally cache the loaded map for quick access later
                self.instances[map_name] = map_instance
                
                return map_instance
        except Exception as e:
            print(f"Failed to load map '{map_name}': {e}")
            return None

    async def load_all_maps(self):
        # Ensure the maps directory exists
        self.maps_path.mkdir(exist_ok=True)
        
        # Iterate through all .map files in the maps directory
        for map_file in self.maps_path.glob("*.map"):
            map_name = map_file.stem  # Extract the map name from the file name (without the .map extension)
            
            try:
                # Open the map file asynchronously
                async with aiofiles.open(map_file, mode='r') as file:
                    map_data = await file.read()
                    map_data_json = json.loads(map_data)
                    
                    # Instantiate the Map object from the loaded data
                    # Ensure your Map class has a corresponding method to handle this.
                    map_instance = Map.from_dict(map_data_json, self.event_dispatcher)
                    
                    # Store the map instance in the instances dictionary
                    self.instances[map_name] = map_instance
            except Exception as e:
                print(f"Failed to load map '{map_name}': {e}")

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
        print(f"subscriptions setup for {self.map_name}")
        self.event_dispatcher.subscribe_internal("add_tile", self.add_tile)
        self.event_dispatcher.subscribe_internal("remove_tile", self.remove_tile)
        self.event_dispatcher.subscribe_internal("add_zone", self.add_zone)
        self.event_dispatcher.subscribe_internal("remove_zone", self.remove_zone)
        self.event_dispatcher.subscribe_internal("join_map", self.join_map)
        self.event_dispatcher.subscribe_internal("leave_map", self.leave_map)
        self.event_dispatcher.subscribe_internal("user_account_login_ok", self.join_map)
        self.event_dispatcher.subscribe_internal("user_account_logout_ok", self.leave_map)

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
        print("join map method called")
        username = event_data['username']
        current_map = event_data['current_map']
        user_instance = event_data['user_instance']
        position = user_instance.get_position()
        logged_in = event_data['logged_in']
        user_data = user_instance.to_dict()
        map_data = self.instances[current_map].to_dict()

        # check the user's current position in comparison to map boundaries
        if position in self.is_within_single_bounds():
            # Add the user instance to the players dictionary under the username key
            self.users[username] = user_instance
            print(f"User {username} added to map {self.map_name}")

        # Update the event dispatcher user to map record
        await self.map_registry.assign_user_to_map(username, self.map_name)

        if logged_in:
            await self.event_dispatcher.dispatch("user_data_update", {
                "message_type": "user_data",
                "username": username,
                "user_data": user_data,
                "map_data": map_data
            })
        else:
            await self.event_dispatcher.dispatch("user_data_update", {
                "message_type": "user_data",
                "username": username,
                "map_data": map_data
            })

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
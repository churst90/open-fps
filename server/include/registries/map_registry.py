# standard imports
import random
from pathlib import Path
import json
import asyncio
from asyncio import Lock
import aiofiles  # Import aiofiles for async file operations

# project specific imports
from include.assets.tile import Tile
from include.assets.zone import Zone
from include.assets.map import Map
from include.registries.map_parser import MapParser

class MapRegistry:
    def __init__(self):
        self.lock = Lock()
        self.instances = {}  # For storing Map instance objects for runtime use
        self.maps_path = Path.cwd() / 'maps'

    async def create_map(self, event_data):
        # Extract the values from the incoming event_data
        username = event_data['username']
        map_name = event_data['map_name']
        map_size = event_data['map_size']
        start_position = event_data['start_position']

        # check to see if the map being created already exists
        if map_name in self.instances:
            return False

        try:
            new_map = Map()
            new_map.set_map_name(map_name)
            new_map.set_map_size(map_size)
            new_map.set_start_position(start_position)
            await new_map.add_owner(username)

            # store the instance in the instances variable
            self.instances[map_name] = new_map

            await self.save_map(map_name)
            return
        except:
            return False

    async def remove_map(self, map_name):
        if map_name in self.instances:
            del self.instances[map_name]
            return
        else:
            return False

    async def get_map_instance(self, name):
        try:
            async with self.lock:
                map_instance = self.instances[name]
                return map_instance
        except:
            return False

    async def get_all_maps(self):
        try:
            async with self.lock:
                return self.instances
        except:
            return False

    async def load_map(self, map_name):
        map_file_path = self.maps_path / f"{map_name}.map"
        
        # Check if the map file exists
        if not map_file_path.exists():
            print(f"Map file for '{map_name}' does not exist.")
            return False
        
        # Read the map file using the custom format
        try:
            async with aiofiles.open(map_file_path, 'r') as file:
                custom_format_str = await file.read()

                # Use MapParser to convert custom format string to map_data dict
                map_data = MapParser.parse_custom_map_format_to_dict(custom_format_str)
                
                # Instantiate the Map object from the map_data dict
                map_instance = Map.from_dict(map_data)
                
                # Add the map instance to the instances dictionary
                self.instances[map_name] = map_instance
                
                return map_instance
        except Exception as e:
            print(f"Failed to load map '{map_name}': {e}")
            return False

    async def load_all_maps(self):
        print("Loading maps ...")
        self.maps_path.mkdir(exist_ok=True)

        map_files = list(self.maps_path.glob("*.map"))
        print(f"Found {len(map_files)} map files.")
        for map_file in map_files:
            map_name = map_file.stem
            print(f"Loading map: {map_name}")
            try:
                async with aiofiles.open(map_file, mode='r') as file:
                    custom_format_str = await file.read()
                    # Parse the custom format string to a dict
                    map_data_dict = MapParser.parse_custom_map_format_to_dict(custom_format_str)

                    map_instance = Map.from_dict(map_data_dict)
                    self.instances[map_name] = map_instance
                    print(f"Loaded map '{map_name}' successfully.")
            except Exception as e:
                print(f"Failed to load map '{map_name}': {e}")

    async def save_map(self, name):
        if name not in self.instances:
            return False

        map_instance = self.instances[name]
        map_data = map_instance.to_dict()
        self.maps_path.mkdir(parents=True, exist_ok=True)

        # Convert map_data dict to custom format string using MapParser
        custom_format_str = MapParser.convert_dict_to_custom_map_format(map_data)

        try:
            async with aiofiles.open(self.maps_path / f'{name}.map', 'w') as file:
                await file.write(custom_format_str)
            print(f"Map '{name}' saved successfully.")
        except Exception as e:
            print(f"Failed to save map '{name}': {e}")
            return False

    async def save_all_maps(self):
        # Ensure the maps directory exists
        self.maps_path.mkdir(parents=True, exist_ok=True)
    
        # Acquire the lock to ensure thread safety
        async with self.lock:
            for map_name, map_instance in self.instances.items():
                map_data_dict = map_instance.to_dict()  # Serialize the map instance to a dictionary
                custom_format_str = MapParser.convert_dict_to_custom_map_format(map_data_dict)
                
                map_file_path = self.maps_path / f'{map_name}.map'  # Define the file path

                try:
                    # Open the file asynchronously and write the custom format string
                    async with aiofiles.open(map_file_path, 'w') as file:
                        await file.write(custom_format_str)
                    print(f"Map '{map_name}' saved successfully.")
                except Exception as e:
                    print(f"Failed to save map '{map_name}': {e}")
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

class MapRegistry:
    def __init__(self):
        self.lock = Lock()
        self.instances = {}  # For storing Map instance objects for runtime use
        self.maps_path = Path('maps')  # Define the maps directory path

    async def create_map(self, event_data):
        # Extract the values from the incoming event_data
        username = event_data['username']
        map_name = event_data['map_name']
        map_size = event_data['map_size']
        start_position = event_data['start_position']

        # check to see if the map being created already exists
        if map_name in self.instances:
            print(f"{map_name} already exists.")
            return False

        # Create a new Map instance using the provided event dispatcher
        try:
            new_map = Map()
            new_map.set_map_name(map_name)
            new_map.set_map_size(map_size)
            new_map.set_start_position(start_position)
            await new_map.add_owner(username)

            # store the instance in the instances variable
            self.instances[map_name] = new_map

            print(f"Default {new_map.map_name} map created successfully with dimensions {new_map.map_size}")

            await self.save_map(map_name)
            return
        except Exception as E:
            print(f"Error creating and saving map: {e}")
            return False

    async def get_map_instance(self, name):
        try:
            async with self.lock:
                map_instance = self.instances[name]
                return map_instance
        except exception as E:
            print(f"unable to retrieve the map instance: {E}")
            return False

    async def get_all_maps(self):
        try:
            async with self.lock:
                return self.instances
        except exception as E:
            print(f"Error returning the map instances: {E}")
            return False

    async def load_map(self, map_name):
        map_file_path = self.maps_path / f"{map_name}.map"
        
        # Check if the map file exists
        if not map_file_path.exists():
            print(f"Map file for '{map_name}' does not exist.")
            return False
        
        # Read the map file
        try:
            async with aiofiles.open(map_file_path, 'r') as file:
                map_data = json.loads(await file.read())
                
                # Instantiate the Map object from the map data
                map_instance = Map.from_dict(map_data, self.event_dispatcher)
                
                # Add the map instance to the instances dictionary
                self.instances[map_name] = map_instance
                
                return map_instance
        except Exception as e:
            print(f"Failed to load map '{map_name}': {e}")
            return False

    async def load_all_maps(self):
        print("Attempting to load maps ...")
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
                    map_instance = Map.from_dict(map_data_json)
                    
                    # Store the map instance in the instances dictionary
                    self.instances[map_name] = map_instance
                    return
            except Exception as e:
                print(f"Failed to load map '{map_name}': {e}")
                return False

    async def save_map(self, name):
        if name not in self.instances:
            print(f"Map '{name}' does not exist.")
            return False

        map_instance = self.instances[name]
        map_data = map_instance.to_dict()
        self.maps_path.mkdir(parents=True, exist_ok=True)
        try:
            async with aiofiles.open(self.maps_path / f'{name}.map', 'w') as file:
                await file.write(json.dumps(map_data))
            print(f"Map '{name}' saved successfully.")
            return
        except Exception as e:
            print(f"Failed to save map {name}: {e}")
            return False
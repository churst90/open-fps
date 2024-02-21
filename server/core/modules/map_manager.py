# standard imports
import random
import json
import asyncio
from asyncio import Lock

# Project specific imports
from core.events.event_dispatcher import EventDispatcher
from core.data import Data

class MapRegistry:
    _lock = Lock()
    _instances = {}  # For storing Map instance objects for runtime use
    _client_data = {}  # For storing instance changes in dictionary-friendly map data for client transmission
    _event_dispatcher = EventDispatcher.get_instance()
    _data = Data()

    @classmethod
    async def selectively_sync_map(cls, name):
        async with cls._lock:
            if name not in cls._instances:
                print(f"Error: Map '{name}' not found.")
                return

            map_instance = cls._instances[name]
            map_data = cls._client_data.get(name, {})
        
            for element_type, keys in map_instance.changed_elements.items():
                for key in keys:
                    if element_type == "tiles":
                        map_data["tiles"][key] = map_instance.tiles[key].to_dict()

                    # Add similar handling for zones, items, and AI

            map_instance.changed_elements = {k: set() for k in map_instance.changed_elements.keys()}
            cls._client_data[name] = map_data

    @classmethod
    async def create_map(cls, name, size):
        if name in cls._instances:
            print("Map already exists")
            raise ValueError(f"A map with the name '{name}' already exists.")
        # Create a new Map instance using the provided event dispatcher
        new_map = Map(cls._event_dispatcher)
        new_map.set_map_name(name)
        new_map.set_map_size(size)
        print(f"Default {new_map.map_name} map created successfully with dimensions {new_map.map_size}")
        # Create the new map instance and its dictionary representation
        cls._instances[name] = new_map
        cls._client_data[name] = new_map.to_dict()
        return cls._client_data[name]

    @classmethod
    async def update_map_data(cls, name):
        async with cls._lock:
            if name in cls._instances:
                cls._client_data[name] = cls._instances[name].to_dict()
            else:
                raise ValueError(f"Map '{name}' not found for update.")

    @classmethod
    async def get_map(cls, name):
        async with cls._lock:
            return cls._client_data.get(name, "Error: Map not found.")

    @classmethod
    async def get_all_maps(cls):
        async with cls._lock:
            return cls._client_data

    @classmethod
    async def get_map_instance(cls, name):
        async with cls._lock:
            return cls._instances.get(name, None)

    @classmethod
    async def save_maps(cls):
        async with cls._lock:
            await cls._data.export(cls._client_data, "maps")
        print("Maps saved successfully")

    @classmethod
    async def load_maps(cls):
        async with cls._lock:
            data = await cls._data.async_init()
            print("Attempting to load maps.dat from disk ...")
            maps_data = await cls._data.load("maps")  # Correct await usage
            if maps_data:
                print("maps.dat loaded successfully")
                for name, map_dict in maps_data.items():
                    cls._instances[name] = Map.from_dict(map_dict)
                    cls._client_data[name] = map_dict
            else:
                print("Creating the default map.")
                await cls.create_map("Main", (10, 10, 10))

class Map:
    def __init__(self, event_dispatcher):
        self.map_name = ""
        self.map_size = ()
        self.players = {}
        self.tiles = {}
        self.zones = {}
        self.items = {}
        self.ai = {}
        self.event_dispatcher = event_dispatcher
        self.event_listeners = {
            'change': []
        }
        self.changed_elements = {"tiles": set(), "zones": set(), "items": set(), "ai": set()}

    def mark_changed(self, element_type, key):
        """Mark an element as changed to be selectively synchronized."""
        if element_type in self.changed_elements:
            self.changed_elements[element_type].add(key)

    def on(self, event_name, listener):
        if event_name not in self.event_listeners:
            self.event_listeners[event_name] = []
        self.event_listeners[event_name].append(listener)

    def emit(self, event_name, data):
        for listener in self.event_listeners.get(event_name, []):
            listener(data)

    def set_map_name(self, name):
        self.map_name = name
        # You might want to dispatch an event here
        # self.event_dispatcher.dispatch('set_map_name', {'map': self.map_name})

    def set_map_size(self, size):
        self.map_size = size
        # Similarly, dispatch an event if needed
        # self.event_dispatcher.dispatch('set_map_size', {'map': self.map_name, 'size': self.map_size})

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
        map_instance = cls(data['map_name'])
        map_instance.map_size = data['map_size']
        map_instance.players = data['players']
        map_instance.tiles = data['tiles']
        map_instance.zones = data['zones']
        map_instance.items = data['items']
        # Handle AI objects separately if they have a specific structure
#        map_instance.ai = {key: AI.from_dict(ai_data) for key, ai_data in data['ai'].items()}
        return map_instance

    def to_dict(self):
        return {
            "map_name": self.map_name,
            "map_size": self.map_size,
            "players": list(self.players.keys()),  # Assuming player names or IDs
            "tiles": self.tiles,
            "zones": self.zones,
            "items": self.items,
            "ai": [ai.to_dict() for ai in self.ai.values()],  # Assuming AI has a to_dict method
            # Exclude event_listeners from serialization
        }
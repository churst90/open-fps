# standard imports
import random
import json
import asyncio

# Project specific imports
from .ai import AI
from .items import Item
from core.events.event_dispatcher import EventDispatcher
from core.data import Data

class MapRegistry:
    _instances = {}  # For storing Map instance objects for runtime use
    _client_data = {}  # For storing instance changes in dictionary-friendly map data for client transmission
    _event_dispatcher = EventDispatcher.get_instance()
    _data = Data()

    @classmethod
    def notify_map_updated(cls, name):
        if name in cls._instances:
            cls.update_map_data(name)

    @classmethod
    async def create_map(cls, name, size):
        if name in cls._instances:
            print("Map already exists")
            raise ValueError(f"A map with the name '{name}' already exists.")
        # Create a new Map instance using the provided event dispatcher
        print("Creating map...")
        new_map = Map(cls._event_dispatcher)
        new_map.set_map_name(name)
        new_map.set_map_size(size)
        print(f"{new_map.map_name} created successfully with dimensions of {new_map.map_size}")
        # Register the new map instance and its dictionary representation
        cls._instances[name] = new_map
        cls._client_data[name] = new_map.to_dict()
        print("Map registered successfully.")
        return cls._client_data[name]

    @classmethod
    def update_map_data(cls, name):
        if name in cls._instances:
            cls._client_data[name] = cls._instances[name].to_dict()
        else:
            raise ValueError(f"Map '{name}' not found for update.")

    @classmethod
    def get_map(cls, name):
        return cls._client_data.get(name, "Error: Map not found.")

    @classmethod
    def get_all_maps(cls):
        return cls._client_data

    @classmethod
    def save_maps(cls):
        cls._data.export(cls._client_data, "maps")
        print("Maps saved successfully")

    @classmethod
    async def load_maps(cls):
        maps_data = cls._data.load("maps")
        if maps_data:
            for name, map_dict in maps_data.items():
                cls._instances[name] = Map.from_dict(map_dict)
        else:
            print("No maps data found or failed to load. Creating the default map.")
            await MapRegistry.create_map("Main", (10, 10, 10))

class Map:
    def __init__(self, event_dispatcher):
        self.map_name = ""
        self.map_size = ()
        self.players = {}
        self.tiles = {}
        self.zones = {}
        self.items = {}
        self.ai = {}
        self.tile_counter = 0
        self.zone_counter = 0
        self.item_counter = 0
        self.ai_counter = 0
        self.event_dispatcher = event_dispatcher
        self.event_listeners = {
            'change': []
        }

    def on(self, event_name, listener):
        if event_name not in self.event_listeners:
            self.event_listeners[event_name] = []
        self.event_listeners[event_name].append(listener)

    def emit(self, event_name, data):
        for listener in self.event_listeners.get(event_name, []):
            listener(data)

    async def add_tile(self, name, tile_size, tile_type, wall):
        if not self.is_within_range_bounds(*tile_size):
            raise ValueError("Tile coordinates are out of map bounds")
        tile_key = f"tile{self.tile_counter}"
        self.tile_counter += 1
        tile = {"name": name, "size": tile_size, "type": tile_type, "wall": wall}
        self.tiles[tile_key] = tile
        # Emitting event through dispatcher
        await self.event_dispatcher.dispatch('tile_added', {'map': self.map_name, 'tile_key': tile_key, 'tile': tile})

    async def remove_tile(self, tile_key):
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            # Emitting event through dispatcher
            await self.event_dispatcher.dispatch('tile_removed', {'map': self.map_name, 'tile_key': tile_key})

    async def add_zone(self, text, zone_size):
        if not self.is_within_range_bounds(*zone_size):
            raise ValueError("Zone coordinates are out of map bounds")
        zone_key = f"zone{self.zone_counter:02d}"
        self.zone_counter += 1
        zone = {"text": text, "size": zone_size}
        self.zones[zone_key] = zone
        await self.event_dispatcher.dispatch('zone_added', {'map': self.map_name, 'zone_key': zone_key, 'zone': zone})

    async def remove_zone(self, zone_key):
        if zone_key in self.zones:
            del self.zones[zone_key]
            await self.event_dispatcher.dispatch('zone_removed', {'map': self.map_name, 'zone_key': zone_key})

    async def add_item(self, item_name, item_location):
        if not self.is_within_single_bounds(*item_location):
            raise ValueError("Item coordinates are out of map bounds")
        item_key = f"item{self.item_counter:02d}"
        self.item_counter += 1
        item = {"name": item_name, "location": item_location}
        self.items[item_key] = item
        await self.event_dispatcher.dispatch('item_added', {'map': self.map_name, 'item_key': item_key, 'item': item})

    async def remove_item(self, item_key):
        if item_key in self.items:
            del self.items[item_key]
            await self.event_dispatcher.dispatch('item_removed', {'map': self.map_name, 'item_key': item_key})

    async def add_ai(self, ai_name, ai_type, ai_position):
        if not self.is_within_single_bounds(*ai_position):
            raise ValueError("AI coordinates are out of map bounds")
        ai_key = f"ai{self.ai_counter:02d}"
        self.ai_counter += 1
        ai = {"name": ai_name, "type": ai_type, "position": ai_position}
        self.ai[ai_key] = ai
        await self.event_dispatcher.dispatch('ai_add', {'map': self.map_name, 'ai_key': ai_key, 'ai': ai})

    async def remove_ai(self, ai_key):
        if ai_key in self.ai:
            del self.ai[ai_key]
            await self.event_dispatcher.dispatch('ai_removed', {'map': self.map_name, 'ai_key': ai_key})

    async def add_player(self, player_username, position):
        if player_username in self.players:
            raise ValueError("Player already exists in this map")
        self.players[player_username] = position
        # Optionally emit an event about player addition
        await self.event_dispatcher.dispatch('player_added', {'map': self.map_name, 'username': player_username, 'position': position})

    async def remove_player(self, player_username):
        if player_username not in self.players:
            raise ValueError("Player does not exist in this map")
       
        # Remove the player from the map's player dictionary
        del self.players[player_username]
       
        # Optionally emit an event about player removal
        await self.event_dispatcher.dispatch('player_removed', {'map': self.map_name, 'username': player_username})

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
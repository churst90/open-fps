# standard imports
import random
import json

# Project specific imports
from ai import AI
from items import Item

class Map:
    def __init__(self, name):
        self.name = name
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

    def add_tile(self, name, tile_size, tile_type, wall):
        if not self.is_within_range_bounds(*tile_size):
            raise ValueError("Tile coordinates are out of map bounds")
        tile_key = f"tile{self.tile_counter:02d}"
        self.tile_counter += 1
        tile = {"name": name, "size": tile_size, "type": tile_type, "wall": wall}
        self.tiles[tile_key] = tile
        self.emit('change', {'action': 'add_tile', 'tile_key': tile_key, 'tile': tile})

    def remove_tile(self, tile_key):
        if tile_key in self.tiles:
            del self.tiles[tile_key]
            self.emit('change', {'action': 'remove_tile', 'tile_key': tile_key})

    def add_zone(self, text, zone_size):
        if not self.is_within_range_bounds(*zone_size):
            raise ValueError("Zone coordinates are out of map bounds")
        zone_key = f"zone{self.zone_counter:02d}"
        self.zone_counter += 1
        zone = {"text": text, "size": zone_size}
        self.zones[zone_key] = zone
        self.emit('change', {'action': 'add_zone', 'zone_key': zone_key, 'zone': zone})

    def remove_zone(self, zone_key):
        if zone_key in self.zones:
            del self.zones[zone_key]
            self.emit('change', {'action': 'remove_zone', 'zone_key': zone_key})

    def add_item(self, item_name, item_location):
        if not self.is_within_single_bounds(*item_location):
            raise ValueError("Item coordinates are out of map bounds")
        item_key = f"item{self.item_counter:02d}"
        self.item_counter += 1
        item = {"name": item_name, "location": item_location}
        self.items[item_key] = item
        self.emit('change', {'action': 'add_item', 'item_key': item_key, 'item': item})

    def remove_item(self, item_key):
        if item_key in self.items:
            del self.items[item_key]
            self.emit('change', {'action': 'remove_item', 'item_key': item_key})

    def spawn_ai(self, ai_name, ai_type, ai_position):
        if not self.is_within_single_bounds(*ai_position):
            raise ValueError("AI coordinates are out of map bounds")
        ai_key = f"ai{self.ai_counter:02d}"
        self.ai_counter += 1
        ai = {"name": ai_name, "type": ai_type, "position": ai_position}
        self.ai[ai_key] = ai
        self.emit('change', {'action': 'spawn_ai', 'ai_key': ai_key, 'ai': ai})

    def remove_ai(self, ai_key):
        if ai_key in self.ai:
            del self.ai[ai_key]
            self.emit('change', {'action': 'remove_ai', 'ai_key': ai_key})

    def set_size(self, size):
        self.map_size = size
        self.emit('change', {'action': 'set_size', 'size': size})

    def is_within_single_bounds(self, x, y, z):
        min_x, max_x, min_y, max_y, min_z, max_z = self.map_size
        return min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z

    def is_within_range_bounds(self, min_x, max_x, min_y, max_y, min_z, max_z):
        map_min_x, map_max_x, map_min_y, map_max_y, map_min_z, map_max_z = self.map_size
        return map_min_x <= min_x <= max_x <= map_max_x and \
               map_min_y <= min_y <= max_y <= map_max_y and \
               map_min_z <= min_z <= max_z <= map_max_z


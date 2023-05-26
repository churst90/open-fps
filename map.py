# standard imports
import random

# Project specific imports
from ai import AI
from items import Item

class Map:
  def __init__(self, name):
    self.name = name
    self.map_size = ()
    self.players = {}
    self.tiles={}
    self.zones = {}
    self.items = {}
    self.ai = {}
    self.tile_counter = 0
    self.zone_counter = 0
    self.item_counter = 0
    self.ai_counter = 0

  def add_tile(self, name, tile_size, tile_type, wall):
    if not self.is_within_range_bounds(tile_size[0], tile_size[1], tile_size[2], tile_size[3], tile_size[4], tile_size[5]):
      raise ValueError("Tile coordinates are out of map bounds")
    else:
      tile_key = f"tile{self.tile_counter:02d}"
      self.tile_counter += 1

    tile = {
        "name": name,
        "size": tile_size,
        "type": tile_type,
        "wall": wall
        }
    self.tiles[tile_key] = tile

  def add_zone(self, text, zone_size):
    if not self.is_within_range_bounds(zone_size[0], zone_size[1], zone_size[2], zone_size[3], zone_size[4], zone_size[5]):
      raise ValueError("Zone coordinates are out of map bounds")
    else:
      zone_key = f"zone{self.zone_counter:02d}"
      self.zone_counter += 1

    zone = {
        "text": text,
        "size": zone_size
        }
    self.zones[zone_key] = zone

  def add_item(self, item_name, item_location):
    new_item = Item()
    new_item.set_name(item_name)

    if not self.is_within_single_bounds(item_location[0], item_location[1], item_location[2]):
      raise ValueError("Item coordinates are out of map bounds")
    else:
      new_item.set_location(item_location)

    item_key = f"item{self.item_counter:02d}"
    self.item_counter += 1

    self.items[item_key] = new_item

  def spawn_ai(self, ai_name, ai_type, ai_position):
    new_ai = AI()
    new_ai.set_name(ai_name)
    new_ai.set_type(ai_type)

    if new_ai.position == None:
      min_x, max_x, min_y, max_y, min_z, max_z = self.map_size
      random_x = random.randint(min_x, max_x)
      random_y = random.randint(min_y, max_y)
      random_z = random.randint(min_z, max_z)
      new_ai.set_position(random_x, random_y, random_z)
    else:
      if not self.is_within_single_bounds(ai_position[0], ai_position[1], ai_position[2]):
        raise ValueError("AI coordinates are out of map bounds")

    new_ai.set_position(ai_position[0], ai_position[1], ai_position[2])

    ai_key = f"ai{self.ai_counter:02d}"
    self.ai_counter += 1

    self.ai[ai_key] = new_ai  # Add the new AI to the dictionary

  def set_size(self, size):
    self.map_size = size

  def is_within_single_bounds(self, x, y, z):
    min_x, max_x, min_y, max_y, min_z, max_z = self.map_size
    return (min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z)

  def is_within_range_bounds(self, min_x, max_x, min_y, max_y, min_z, max_z):
    min_x_map, max_x_map, min_y_map, max_y_map, min_z_map, max_z_map = self.map_size
    return ( min_x_map <= min_x <= max_x <= max_x_map and min_y_map <= min_y <= max_y <= max_y_map and min_z_map <= min_z <= max_z <= max_z_map)

  def simplify_map(self, condition=None):
    print(self.players)
    simplified_map = {
      'name': self.name,
      'map_size': self.map_size,
      'tiles': self.tiles,
      'zones': self.zones,
      'items': self.items,
      'ai': {key: vars(ai) for key, ai in self.ai.items()},
      'players': {
          key: {attr: value for attr, value in vars(player_tuple[0]).items() if attr != 'inventory'}
          for key, player_tuple in self.players.items()
      }, }

    if condition == 'player_joined':
      # Send update to client about the new player
      pass
    elif condition == 'player_left':
      # Send update to client about the player who left
      pass
    elif condition == 'player_changed_map':
      # Send the new simplified map to the client
      return simplified_map
    elif condition == 'items_changed':
      # Send update to client about the items
      pass
    elif condition == 'ai_changed':
      # Send update to client about the AI
      pass

    return simplified_map
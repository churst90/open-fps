import uuid

class Tile:
    def __init__(self, tile_position, tile_type, is_wall):
        self.tile_key = str(uuid.uuid4())  # Generate a unique identifier
        self.tile_position = tile_position  # Assuming tile_position is a tuple (x1, x2, y1, y2, z1, z2)
        self.tile_type = tile_type  # Could be an enum, string, or numeric identifier
        self.is_wall = is_wall  # Boolean indicating if the tile blocks movement
        tile_types = ["air", "brick", "concrete", "cement", "dirt", "grass", "glass", "ice", "leaves", "mud", "wood", "water"]

    def update_tile_position(self, new_position):
        self.tile_position = new_position

    def update_tile_type(self, new_type):
        if new_type in tile_types:
            self.tile_type = new_type
        else:
            print("invalid tile type")

    def set_is_wall(self, bool_value):
        self.is_wall = bool_value

    def to_dict(self):
        return {
            "tile_key": self.tile_key,
            "tile_position": self.tile_position,
            "tile_type": self.tile_type,
            "is_wall": self.is_wall
        }

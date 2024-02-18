class Tile:
    def __init__(self, tile_position, tile_type, is_wall):
        self.tile_position = tile_position  # Assuming tile_position is a tuple (x, y, z)
        self.tile_type = tile_type  # Could be an enum, string, or numeric identifier
        self.is_wall = is_wall  # Boolean indicating if the tile blocks movement

    def update_tile_position(self, new_position):
        self.tile_position = new_position

    def update_tile_type(self, new_type):
        self.tile_type = new_type

    def set_is_wall(self, bool_value):
        self.is_wall = bool_value

    def to_dict(self):
        return {
            "tile_position": self.tile_position,
            "tile_type": self.tile_type,
            "is_wall": self.is_wall
        }

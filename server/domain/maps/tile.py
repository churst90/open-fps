# domain/maps/tile.py

class Tile:
    TILE_TYPES = [
        "air", "brick", "concrete", "cement", "dirt", "grass",
        "glass", "ice", "leaves", "mud", "wood", "water"
    ]

    def __init__(self, tile_type: str, tile_position: tuple, is_wall: bool):
        # tile_position is now a tuple of floats (x1, x2, y1, y2, z1, z2)
        # Ensure that tile_position consists of floats
        self.tile_position = tuple(float(v) for v in tile_position)
        self.tile_type = tile_type
        self.is_wall = is_wall

    def to_dict(self):
        return {
            "tile_position": self.tile_position,
            "tile_type": self.tile_type,
            "is_wall": self.is_wall
        }

    @classmethod
    def from_dict(cls, data):
        return cls(
            tile_type=data["tile_type"],
            tile_position=tuple(float(v) for v in data["tile_position"]),
            is_wall=data["is_wall"]
        )

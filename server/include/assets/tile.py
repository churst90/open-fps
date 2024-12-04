import uuid

class Tile:
    TILE_TYPES = ["air", "brick", "concrete", "cement", "dirt", "grass", "glass", "ice", "leaves", "mud", "wood", "water"]

    def __init__(self, tile_position, tile_type, is_wall):
        self._tile_key = str(uuid.uuid4())  # Generate a unique identifier
        self._tile_position = None
        self._tile_type = None
        self._is_wall = None

        self.tile_position = tile_position  # Use setter for validation
        self.tile_type = tile_type  # Use setter for validation
        self.is_wall = is_wall  # Use setter for validation

    @property
    def tile_key(self):
        """Read-only property for the unique tile identifier."""
        return self._tile_key

    @property
    def tile_position(self):
        """Get or set the position of the tile."""
        return self._tile_position

    @tile_position.setter
    def tile_position(self, value):
        if isinstance(value, tuple) and len(value) == 6 and all(isinstance(v, (int, float)) for v in value):
            self._tile_position = value
        else:
            raise ValueError("Tile position must be a tuple of six numbers (x1, x2, y1, y2, z1, z2).")

    @property
    def tile_type(self):
        """Get or set the type of the tile."""
        return self._tile_type

    @tile_type.setter
    def tile_type(self, value):
        if value in Tile.TILE_TYPES:
            self._tile_type = value
        else:
            raise ValueError(f"Invalid tile type: {value}. Must be one of {Tile.TILE_TYPES}.")

    @property
    def is_wall(self):
        """Get or set whether the tile blocks movement."""
        return self._is_wall

    @is_wall.setter
    def is_wall(self, value):
        if isinstance(value, bool):
            self._is_wall = value
        else:
            raise ValueError("is_wall must be a boolean value.")

    def to_dict(self):
        """Convert the tile instance to a dictionary."""
        return {
            "tile_key": self.tile_key,
            "tile_position": self.tile_position,
            "tile_type": self.tile_type,
            "is_wall": self.is_wall
        }

    @classmethod
    def from_dict(cls, data):
        """Create a Tile instance from a dictionary."""
        return cls(
            tile_position=data['tile_position'],
            tile_type=data['tile_type'],
            is_wall=data['is_wall']
        )

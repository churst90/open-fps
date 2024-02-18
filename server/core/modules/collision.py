class Collision:
    def __init__(self, game_map):
        self.game_map = game_map

    def is_move_valid(self, x, y, z):
        """Determine if a move to a new position is valid."""
        if not self._is_within_boundaries(x, y, z):
            return False, "Out of bounds"

        if not self._is_position_walkable(x, y, z):
            return False, "Blocked by obstacle"

        return True, "Move valid"

    def _is_within_boundaries(self, x, y, z):
        """Check if the target position is within map boundaries."""
        return 0 <= x < self.game_map.width and 0 <= y < self.game_map.height and 0 <= z < self.game_map.depth

    def _is_position_walkable(self, x, y, z):
        """Check if the target position is walkable (not an obstacle)."""
        return self.game_map.is_position_walkable(x, y, z)

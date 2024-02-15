class Collision:
    def __init__(self, game_map):
        self.game_map = game_map

    def is_move_valid(self, x, y, z):
        # Check if the target position is within map boundaries
        if not self.check_boundary_collision(x, y, z):
            return False  # Movement outside the map boundaries is not allowed
        
        # Check if the target position is walkable (not an obstacle)
        if not self.game_map.is_position_walkable(x, y, z):
            return False  # Movement into obstacles is not allowed

        # If no collisions were detected, the move is valid
        return True

    def check_boundary_collision(self, x, y, z):
        # Assuming the game_map object has attributes defining its boundaries
        return (0 <= x < self.game_map.width and
                0 <= y < self.game_map.height and
                0 <= z < self.game_map.depth)

    # This method assumes a simple method on the map to check if a position is walkable
    # It abstracts away the specifics of how the map's data is structured

class CollisionManager:

    @staticmethod
    def is_move_valid(map_instance, new_position, player_id=None):
        x, y, z = new_position

        # Check if the move is within map boundaries
        if not CollisionManager.is_within_single_bounds(map_instance.map_size, x, y, z):
            return False, "Out of bounds"
        # Check if the new position is walkable (not blocked by an obstacle)
        if not CollisionManager.is_position_walkable(map_instance.tiles, x, y, z):
            return False, "Blocked by obstacle or wall"
        # Check if the new position is occupied by another player or entity
        if CollisionManager.is_position_occupied(map_instance.users, new_position, player_id):
            return False, "Position occupied by another player or entity"

        return True, "Move valid"

    @staticmethod
    def is_within_single_bounds(map_size, x, y, z):
        # Unpack the map_size tuple into minimum and maximum coordinates
        min_x, max_x, min_y, max_y, min_z, max_z = map_size
        # Check if the coordinates are within the map bounds
        return min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z

    @staticmethod
    def is_within_range_bounds(map_size, min_x, min_y, min_z, max_x, max_y, max_z):
        map_min_x, map_max_x, map_min_y, map_max_y, map_min_z, map_max_z = map_size
        return (map_min_x <= min_x <= map_max_x and map_min_y <= min_y <= map_max_y and
                map_min_z <= min_z <= map_max_z and map_min_x <= max_x <= map_max_x and
                map_min_y <= max_y <= map_max_y and map_min_z <= max_z <= map_max_z)

    @staticmethod
    def is_position_walkable(tiles, x, y, z):
        # Iterate through all tiles to find if the position is walkable
        for tile in tiles.values():
            if tile['tile_position'] == (x, y, z) and not tile['is_wall']:
                return True
        return False

    @staticmethod
    def is_position_occupied(users, new_position, player_id=None):
        # Check if the new position is currently occupied by another user, excluding the player initiating the move
        for user_id, user in users.items():
            if user_id != player_id and user['position'] == new_position:
                return True
        return False

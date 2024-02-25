class Collision:

    @classmethod
    def is_move_valid(cls, map_instance, new_position, player_id=None):
        x, y, z = new_position
    
        if not is_within_single_bounds(map_instance, x, y, z):
            return False, "Out of bounds"

        if not is_position_walkable(map_instance, x, y, z):
            return False, "Blocked by obstacle or wall"

        if is_position_occupied(map_instance, x, y, z, player_id):
            return False, "Position occupied by player or item"

        return True, "Move valid"

    def is_within_single_bounds(map_instance, x, y, z):
        min_x, min_y, min_z = 0, 0, 0
        max_x, max_y, max_z = map_instance.map_size
        return min_x <= x < max_x and min_y <= y < max_y and min_z <= z < max_z

    def is_position_walkable(map_instance, x, y, z):
        for tile_key, tile in map_instance.tiles.items():
            if tile['tile_position'] == (x, y, z) and not tile['is_wall']:
                return True
        return False

    def is_position_occupied(map_instance, x, y, z, player_id=None):
        for other_player_id, player_info in map_instance.players.items():
            if other_player_id != player_id and player_info['position'] == (x, y, z):
                return True
        # Check for items if necessary
        return False

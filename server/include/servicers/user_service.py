class UserService:
    def __init__(self, user_registry, map_registry):
        self.user_registry = user_registry
        self.map_registry = map_registry

    async def move_user(self, username, direction, distance):
        # Logic to calculate the new position
        user = self.user_registry.get_user_instance(username)
        new_position = calculate_new_position(user, direction, distance)
        # Validate the move with the map
        map_instance = self.map_registry.get_map(user.current_map)
        if map_instance.validate_move(new_position):
            user.set_position(new_position)
            return True, new_position
        return False, None

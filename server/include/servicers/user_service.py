class UserService:
    def __init__(self, user_registry, map_registry):
        self.user_registry = user_registry
        self.map_registry = map_registry

    async def create_account(self, event_data, role_manager):
        success = await self._create_account_impl(event_data, role_manager)
        if not success:
            return False

        return

    async def _create_account_impl(self, new_user_data, role_manager):
        try:
            await self.user_registry.create_account(new_user_data, role_manager)
            return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error creating new user account.")
        return False

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

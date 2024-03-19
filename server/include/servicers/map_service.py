class MapService:

    def __init__(self, map_registry, event_dispatcher, role_manager):
        self.map_reg = map_registry
        self.event_dispatcher = event_dispatcher
        self.role_manager = role_manager

    async def update_event_dispatcher_user_to_map(self, username, map_name, action):
        # this method updates the dictionary in event dispatcher for user to map mappings

        if action == "add":
            await self.event_dispatcher.update_user_map(username, map_name, action)
        if action == "remove":
            await self.event_dispatcher.update_user_map(username, map_name, action)

    def validate_move(self, map_name, new_position):
        # Logic to check if the move is valid within the specified map
        map_instance = self.map_registry.get_map(map_name)
        return map_instance.validate_move(new_position)

    async def add_tile(self, username, map_name, tile_data):
        # Check permission first
        if not self.role_manager.has_permission(username, 'add_tile'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for adding tiles on {map_name}.")

        # If permission granted, attempt to add the tile
        success = await self._add_tile_impl(map_name, tile_data)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _add_tile_impl(self, map_name, tile_data):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.add_tile(tile_data)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error adding the tile to {map_name}: {e}")
        return False

    async def add_zone(self, username, map_name, zone_data):
        # Check permission first
        if not self.role_manager.has_permission(username, 'add_zone'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for adding zones on {map_name}.")

        # If permission granted, attempt to add the zone
        success = await self._add_zone_impl(map_name, zone_data)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _add_zone_impl(self, map_name, zone_data):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.add_zone(zone_data)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error adding the zone to {map_name}: {e}")
        return False

    async def join_map(self, map_name, username, user_data):
        # Check permission first
        if not self.role_manager.has_permission(username, 'join_map'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for adding users.")

        # If permission granted, attempt to add the zone
        success = await self._join_map_impl(map_name, user_data)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _join_map_impl(self, map_name, user_data):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.join_map(user_data)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error adding user to {map_name}: {e}")
        return False

    async def leave_map(self, map_name, username, user_data):
        # Check permission first
        if not self.role_manager.has_permission(username, 'leave_map'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for removing users from maps.")

        # If permission granted, attempt to remove the user from the specified map
        success = await self._leave_map_impl(map_name, user_data)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _leave_map_impl(self, map_name, user_data):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.leave_map(user_data)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error removing user from {map_name}: {e}")
        return False

    async def create_map(self, map_name, username, map_data):
        # Check permission first
        if not self.role_manager.has_permission(username, 'create_map'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for creating maps.")

        # If permission granted, attempt to create the map
        success = await self._create_map_impl(map_name, username, map_data)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _create_map_impl(self, map_name, username, map_data):
        try:
            await self.map_reg.create_map(map_data)
            return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error creating map.")
        return False

    async def remove_map(self, map_name, username):
        # Check permission first
        if not self.role_manager.has_permission(username, 'remove_map'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for removing maps.")

        # If permission granted, attempt to remove the map
        success = await self._remove_map_impl(map_name)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _remove_map_impl(self, map_name):
        try:
            await self.map_reg.remove_map(map_name)
            return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error removing {map_name}.")
        return False

    async def add_owner(self, map_name, username):
        # Check permission first
        if not self.role_manager.has_permission(username, 'add_owner'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for adding owners.")

        # If permission granted, attempt to add the new owner to the map
        success = await self._add_owner_impl(map_name, username)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _add_owner_impl(self, map_name, username):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.add_owner(username)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error adding {username} as owner to {map_name}: {e}")
        return False

    async def remove_owner(self, map_name, username):
        # Check permission first
        if not self.role_manager.has_permission(username, 'remove_owner'):
            # Using exceptions for exceptional conditions (e.g., lack of permission)
            raise PermissionError(f"Permission denied for removing owners.")

        # If permission granted, attempt to remove the new owner from the map
        success = await self._remove_owner_impl(map_name, username)
        if not success:
            # You could log or raise an exception here if needed
            return False

        return True

    async def _remove_owner_impl(self, map_name, username):
        try:
            map_instance = self.map_reg.get_map_instance(map_name)
            if map_instance:
                map_instance.remove_owner(username)
                return True
        except Exception as e:
            # Log the error or handle it as needed
            print(f"Error removeing {username} as owner from {map_name}: {e}")
        return False

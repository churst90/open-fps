class UserService:
    def __init__(self, user_registry, map_registry, event_dispatcher):
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.event_dispatcher = event_dispatcher

    async def login(self, username, password):
        user_instance = await self.user_registry.get_user_instance(username)
        map_name = user_instance.get_current_map()

        user_data = await self.user_registry.authenticate_user(username, password)
        if user_data:
            map_instance = await self.map_registry.get_map_instance(map_name)
            map_instance.join_map(username, user_instance)
            await self.update_event_dispatcher_user_to_map(username, map_name, "add")
            return user_data

    async def logout(self, username, map_name):
        map_instance = await self.map_registry.get_map_instance(map_name)
        map_instance.leave_map(username)

        # Call the deauthenticate_user method of the user_registry to log the user out
        success = await self.user_registry.deauthenticate_user(username)
        if success:
            await self.update_event_dispatcher_user_to_map(username, map_name, "remove")
            return
        else:
            return False

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
        user = await self.user_registry.get_user_instance(username)
        if not user:
            return False

        # Calculate new position (you might already have logic in UserMovement)
        new_position = calculate_new_position(user.position, direction, distance, user.yaw, user.pitch)

        # Perform collision check (using Collision module and possibly MapService)
        map_instance = await self.map_registry.get_map(user.current_map)
        valid_move, message = Collision.is_move_valid(map_instance, new_position, username)

        if valid_move:
            # Update user position
            user.set_position(new_position)
            # Notify success
            return new_position
        else:
            return False

    async def turn_user(self, username, turn_direction):
        user = await self.user_registry.get_user_instance(username)
        if not user:
            return False

        if turn_direction == "left":
            user.yaw = (user.yaw - yaw_step) % 360
        elif turn_direction == "right":
            user.yaw = (user.yaw + yaw_step) % 360
        elif turn_direction == "up":
            user.pitch = max(min(user.pitch + pitch_step, 90), -90)
        elif turn_direction == "down":
            user.pitch = max(min(user.pitch - pitch_step, -90), 90)
        else:
            return False

        # Assuming pitch is limited to [-90, 90] and yaw [0, 360)
        user.pitch = max(-90, min(90, user.pitch))
        user.yaw %= 360

        return (user.yaw, user.pitch)

    async def update_event_dispatcher_user_to_map(self, username, map_name, action):
        # this method updates the dictionary in event dispatcher for user to map mappings

        if action == "add":
            await self.event_dispatcher.update_user_map(username, map_name, action)
        if action == "remove":
            await self.event_dispatcher.update_user_map(username, map_name, action)

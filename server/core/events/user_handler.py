import bcrypt
import math
import json
from core.events.event_handler import EventHandler
from core.events.event_dispatcher import EventDispatcher
from core.modules.collision import Collision

class UserHandler(EventHandler):
    def __init__(self, user_reg, map_reg, event_dispatcher):
        super().__init__(event_dispatcher)
        self.user_reg = user_reg
        self.map_reg = map_reg

    async def handle_user_authentication(self, data):
        action = data.get('action')
        username = data.get('username')
        password = data.get('password')

        if action == 'login':
            user = await self.user_reg.load_user(username)
            if user is None:
                await self.event_dispatcher.dispatch("handle_login", {"message_type": "login_failed", "error": "User does not exist"}, scope = "private", recipient = username)
                return

            stored_hashed_password = user.password.encode('utf-8')
            if bcrypt.checkpw(password.encode('utf-8'), stored_hashed_password):
#                await self.user_reg.register_user(username, user)
                user_data = user.to_dict()
                map_instance = await self.map_reg.get_map_instance(user.current_map)
                map_data = map_instance.to_dict() if map_instance else {}
                await self.event_dispatcher.dispatch("handle_login", {"message_type": "login_ok", "user_data": user_data, "map_data": map_data}, scope = "private", recipient = "username")
            else:
                await self.event_dispatcher.dispatch("handle_login", {"message_type": "login_failed", "error": "Incorrect password"}, scope = "private", recipient = username)

        elif action == 'logout':
            # Verify if the user exists and is logged in before logging out
            user = await self.user_reg.get_user_instance(username)
            if user and user.logged_in:
                await self.user_reg.deregister_user(username)
                await self.event_dispatcher.dispatch("user_logout", {"message_type": "logout_ok", "username": username}, scope = "private", recipient = username)
            else:
                await self.event_dispatcher.dispatch("user_logout", {"message_type": "logout_failed", "error": "User not logged in or does not exist"}, scope = "private", recipient = username)

    async def create_account(self, data):
        username = data.get('username')
        password = data.get('password')

        try:
            user_file_path = self.user_reg.users_path / f"{username}.usr"
            if user_file_path.exists():
                raise ValueError("Username already exists")

            await self.user_reg.create_user(username, password)
            user_instance = await self.user_reg.get_user_instance(username)
            user_data = user_instance.to_dict()

            map_instance = await self.map_reg.get_map_instance(user_instance.current_map)
            map_data = map_instance.to_dict() if map_instance else {}

            await self.event_dispatcher.dispatch("handle_create_account", {
                "message_type": "create_account_ok",
                "user_data": user_data,
                "map_data": map_data,
            },
            scope = "private", recipient = username)
        except Exception as e:
            await self.event_dispatcher.dispatch("create_account", {
                "message_type": "create_account_failed",
                "error": str(e)
            },
            scope = "private", recipient = username)

    async def move_user(self, username, direction, distance):
        user = self.user_reg.get_user_instance(username)
        if user:
            dx, dy, dz = await self.calculate_movement_vector(direction, distance, user.yaw, user.pitch)
            new_position = (user.position[0] + dx, user.position[1] + dy, user.position[2] + dz)

            map_instance = self.map_reg.get_map(user.current_map)  # Ensure you get the map instance
            if map_instance:  # Check if map_instance is valid before checking valid_move
                valid_move, message = Collision.is_move_valid(map_instance, new_position, username)

                if valid_move:
                    user.update_position(new_position)  # Update position only on valid move
                    await self.event_dispatcher.dispatch("user_move", {"message_type": "user_move", "username": username, "position": new_position}, scope = "map", map_id = map_instance.get_map_name())
                else:
                    await self.event_dispatch.dispatch("user_move", {"message_type": "user_move", "username": username, "error": message}, scope = "map", map_id = map_instance.get_map_name())
            else:
                await self.event_dispatcher.dispatch("user_move", {"message_type": "user_move", "username": username, "error": "Invalid map instance"}, scope = "map", map_id = map_instance.get_map_name())

    async def turn(self, data):
        username = data.get('username')
        map_name = data.get('map_name')
        turn_direction = data.get('turn_direction')
        user = self.user_reg.get_user_instance(username)
        if not user:
            await self.event_dispatcher("user_turn", {"message_type": "user_turn", "error": "user not found"}, scope = "private", recipient = username)
            return

        # Adjust yaw and pitch based on turn direction
        yaw_step, pitch_step = 1, 1  # Define steps for turning
        if turn_direction == "left":
            user.yaw = (user.yaw - yaw_step) % 360
        elif turn_direction == "right":
            user.yaw = (user.yaw + yaw_step) % 360
        elif turn_direction == "up":
            user.pitch = max(min(user.pitch + pitch_step, 90), -90)
        elif turn_direction == "down":
            user.pitch = max(min(user.pitch - pitch_step, -90), -90)

        # Notify user of their new orientation
        await self.event_dispatcher.dispatch("user_turn", {"message_type": "user_turn", "username": username, "pitch": user.pitch, "yaw": user.yaw}, scope = "map", map_id = map_name)

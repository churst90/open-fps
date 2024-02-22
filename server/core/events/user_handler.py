import bcrypt
import math
import json
from core.events.event_handler import EventHandler
from core.events.event_dispatcher import EventDispatcher

class UserHandler(EventHandler):
    def __init__(self, user_reg, map_reg, event_dispatcher):
        super().__init__(event_dispatcher)
        self.user_reg = user_reg
        self.map_reg = map_reg

    @staticmethod
    def calculate_movement_vector(direction, distance, yaw, pitch):
        # Convert angles from degrees to radians
        yaw_radians = math.radians(yaw)
        pitch_radians = math.radians(-pitch)  # Inverting pitch for intuitive controls

        # Calculate the components of the direction vector for forward movement
        dx = math.cos(pitch_radians) * math.cos(yaw_radians)
        dy = math.sin(pitch_radians)
        dz = math.cos(pitch_radians) * math.sin(yaw_radians)

        # Adjusting the vector based on the specified direction
        if direction == "forward":
            return dx * distance, dy * distance, dz * distance
        elif direction == "backward":
            return -dx * distance, -dy * distance, -dz * distance
        elif direction == "left" or direction == "right":
            # Left and right movement should be on the horizontal plane, so ignore pitch
            side_step = distance if direction == "right" else -distance
            side_dx = math.cos(yaw_radians - math.pi / 2)
            side_dz = math.sin(yaw_radians - math.pi / 2)
            return side_dx * side_step, 0, side_dz * side_step
        elif direction == "up":
            # Direct upward or downward movement, ignoring orientation
            return 0, distance, 0
        elif direction == "down":
            return 0, -distance, 0

    async def authenticate_user(self, data):
        action = data.get('action')
        username = data.get('username')
        password = data.get('password')

        if action == 'login':
            users = self.user_reg._users
            if username in users.keys():
                stored_hashed_password = users[username]['password'].encode('utf-8')
                if bcrypt.checkpw(password.encode('utf-8'), stored_hashed_password):
                    player_obj = self.user_reg.get_user_instance(username)
                    await self.user_reg.register_user(username, player_obj)
                    await self.emit_event("handle_login", users[username])
                else:
                    # Dispatch an event message indicating an incorrect password
                    await self.emit_event("handle_login", {"message_type": "handle_login", "error": "password issue"})
            else:
                # Dispatch a message stating the username doesn't exist
                await self.emit_event("handle_login", {"message_type": "handle_login", "error": "username issue"})

        if action == 'logout':
            # Check if the user is actually online before proceeding
            if await self.user_reg.get_all_users()[username][logged_in] == True:
                self.user_reg.deregister_user(username)
                # dispatch an event the user went offline

    async def move(self, data):
        username = data.get('username')
        direction = data.get('direction')
        distance = data.get('distance')
        user = self.user_reg.get_user_instance(username)
        if not user:
            await self.emit_event("user_move", {"message_type": "user_action", "username": username, "error": "User not found"})

        # Calculate the movement vector based on the user's current orientation
        dx, dy, dz = self.calculate_movement_vector(direction, distance, user.yaw, user.pitch)

        # Proposed new position based on the calculated movement vector
        new_x = user.position[0] + dx
        new_y = user.position[1] + dy
        new_z = user.position[2] + dz

        # Implement collision detection and validation
        map_instance = self.map_reg.get_map(user.current_map)
        # Assuming Collision class exists and has been properly integrated
        collision_detector = Collision(map_instance)
        if not collision_detector.is_move_valid(new_x, new_y, new_z):
            await self.emit_event("user_move", {"message_type": "user_move", "username": username, "error": "can't move there"})

        # Update user's position
        user.update_position((new_x, new_y, new_z))
        # Notify user of successful movement
        await self._emit_event("user_move", {"message_type": "user_move", "direction": direction, "distance": distance})

    async def turn(self, data):
        username = data.get('username')
        turn_direction = data.get('turn_direction')
        user = self.user_reg.get_user_instance(username)
        if not user:
            await self.emit_event("user_turn", {"message_type": "user_turn", "error": "user not found"})
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
        await self.emit_event("user_turn", {"message_type": "user_turn", "username": username, "pitch": user.pitch, "yaw": user.yaw})

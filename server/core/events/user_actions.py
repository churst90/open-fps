import math
import json
from core.events.event_handler import EventHandler

class UserActions(EventHandler):
    def __init__(self, network, user_registry, map_registry, event_dispatcher):
        super().__init__(network, event_dispatcher)
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.network = network

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

    async def move(self, username, direction, distance=1):
        user = self.user_registry.get_user_instance(username)  # Ensure correct method is called
        if not user:
            return  # User not found

        # Calculate the movement vector based on the user's current orientation
        dx, dy, dz = self.calculate_movement_vector(direction, distance, user.yaw, user.pitch)

        # Proposed new position based on the calculated movement vector
        new_x = user.position[0] + dx
        new_y = user.position[1] + dy
        new_z = user.position[2] + dz

        # Implement collision detection and validation
        map_instance = self.map_registry.get_map(user.current_map)
        # Assuming Collision class exists and has been properly integrated
        collision_detector = Collision(map_instance)
        if not collision_detector.is_move_valid(new_x, new_y, new_z):
            await self._notify_user(username, "Movement blocked by obstacle", success=False)
            return

        # Update user's position
        user.update_position((new_x, new_y, new_z))
        # Notify user of successful movement
        await self._notify_user(username, "Move successful", new_position=(new_x, new_y, new_z), success=True)

    async def turn(self, username, turn_direction):
        user = self.user_registry.get_user_instance(username)  # Ensure correct method is called
        if not user:
            await self._notify_user(username, "User not found", success=False)
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
        await self._notify_user(username, "Turn successful", orientation_update={"yaw": user.yaw, "pitch": user.pitch}, success=True)

    async def _notify_user(self, username, message, new_position=None, orientation_update=None, success=True):
        writer = await self.network.get_writer(username)
        if writer:
            payload = {"message_type": "user_action_result", "action_type": "update", "success": success, "message": message}
            if new_position:
                payload["new_position"] = new_position
            if orientation_update:
                payload["orientation_update"] = orientation_update
            await self.network.send(json.dumps(payload), writer)

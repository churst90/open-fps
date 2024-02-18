import math
from core.events.event_handler import EventHandler

class UserActions(EventHandler):
    def __init__(self, network, user_registry, map_registry, event_dispatcher):
        super().__init__(network, event_dispatcher)
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.network = network

    async def move(self, username, direction, distance=1):
        user = self.user_registry.get_user(username)
        if not user:
            return  # User not found

        # Calculate the new position based on direction and distance
        x, y, z = user.position  # Assume user.position returns a tuple (x, y, z)
        dx, dy, dz = 0, 0, 0  # Initialize movement deltas
    
        if direction == "forward":
            dz += distance
        elif direction == "backward":
            dz -= distance
        elif direction == "left":
            dx -= distance
        elif direction == "right":
            dx += distance
        elif direction == "up":  # Assuming 'up' moves along the Y-axis
            dy += distance
        elif direction == "down":
            dy -= distance

        # Proposed new position
        new_x, new_y, new_z = x + dx, y + dy, z + dz

        # Collision detection
        map_instance = self.map_registry.get_map(user.current_map)
        collision_detector = Collision(map_instance)
    
        # Check for wall and boundary collisions
        if collision_detector.check_wall_collision(new_x, new_y, new_z) or collision_detector.check_boundary_collision(new_x, new_y, new_z):
            # don't send anything for now
            pass

        # If no collision, update user's position and notify
        user.update_position((new_x, new_y, new_z))
        writer = await self.network.get_writer(username)
        if writer:
            # Constructing a message payload
            message = {"message_type": "user_action_result", "action_type": "move", "position_update": user.get_position()}
            await self.network.send(message, writer)
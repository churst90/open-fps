import math

class UserActions(EventHandler):
    def __init__(self, network, user_registry, map_registry, event_dispatcher):
        super().__init__(network, event_dispatcher)
        self.user_registry = user_registry
        self.map_registry = map_registry

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
            # Handle collision (e.g., notify user, cancel movement)
            await self.network.send_update(username, {"type": "collision", "message": "Movement blocked."})
            return

        # If no collision, update user's position and notify
        user.set_position((new_x, new_y, new_z))
        await self.network.send_update(username, {"type": "position_update", "new_position": (new_x, new_y, new_z)})
        await self.event_dispatcher.dispatch("PlayerPositionChanged", {"username": username, "new_position": (new_x, new_y, new_z)})

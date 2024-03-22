class UserMovement:

    @classmethod
    def calculate_movement_vector(cls, direction, distance, yaw, pitch):
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

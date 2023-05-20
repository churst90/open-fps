import math
from collision import Collision

class Player:
    def __init__(self):
        self.username = ""
        self.logged_in = 0
        self.map = {}
        self.position = []
        self.zone = ""
        self.yaw = 0
        self.pitch = 0
        self.direction = ()
        self.health = None
        self.energy = None
        self.pvp_status = False
        self.rank = None
        self.inventory = {}

    def turn(self, data):
        turn_speed = 1  # You can adjust this value

        direction = data.get('direction')

        if direction == "left":
            self.yaw = (self.yaw - turn_speed) % 360
        elif direction == "right":
            self.yaw = (self.yaw + turn_speed) % 360

        new_pitch = data.get('pitch')
        if new_pitch is not None:
            self.pitch = max(-90, min(90, new_pitch))

        pitch = math.radians(self.pitch)
        yaw = math.radians(self.yaw)

        dx = math.cos(pitch) * math.cos(yaw)
        dy = math.cos(pitch) * math.sin(yaw)
        dz = math.sin(pitch)

        self.direction = (dx, dy, dz)

        message = {
            "type": "turn",
            "map": self.map,
            "username": self.username,
            "direction": self.direction,
            "yaw": self.yaw,
            "pitch": self.pitch
        }
        return message

    def move(self, data):
        collision = Collision(self.map)
        direction = self.direction
        right_vector = [0, 0, 0]
        right_vector[0] = direction[1]
        right_vector[1] = -direction[0]
        right_vector[2] = 0
        norm = (right_vector[0] ** 2 + right_vector[1] ** 2 + right_vector[2] ** 2) ** 0.5
        if norm == 0:
            return {"type": "error", "message": "invalid move"}

        right_vector = [right_vector[0] / norm, right_vector[1] / norm, right_vector[2] / norm]

        move = data["move"]
        delta = []
        if move == "left":
            delta = [-right_vector[0], -right_vector[1], -right_vector[2]]
        elif move == "right":
            delta = [right_vector[0], right_vector[1], right_vector[2]]
        elif move == "forward":
            delta = direction
        elif move == "backward":
            delta = [-direction[0], -direction[1], -direction[2]]
        elif move == "up":
            delta = [0, 0, 1]
        elif move == "down":
            delta = [0, 0, -1]
        else:
            return {"type": "error", "message": "invalid move"}

        x, y, z = self.position[0] + delta[0], self.position[1] + delta[1], self.position[2] + delta[2]

        if collision.check_boundary_collision([x, y, z]):
            return {"type": "error", "message": "edge of the map"}
        elif collision.check_wall_collision(self.position):
            return {"type": "error", "message": "wall"}

        self.position = [x, y, z]

        update_message = {
            "type": "move",
            "username": data["username"],
            "position": [x, y, z],
            "tile_type": self.get_current_tile_type()  # Add the tile_type field
        }
        return update_message

    def set_position(self, position):
        self.position = position

    def set_health(self, health):
        self.health = health

    def set_energy(self, energy):
        self.energy = energy

    def set_zone(self):
        pass

    def set_username(self, username):
        self.username = username

    def set_map(self, map_dict, map_name):
        self.map = map_dict[map_name].name

    def set_login(self, login):
        self.logged_in = login

    def get_zone(self):
        return self.zone

    def get_current_tile_type(self):
        # Get the player's current position
        x, y, z = self.position

        # Retrieve the map object based on the player's current map
        current_map = self.map

        # Retrieve the map's tile data
        tiles = current_map.tiles

        # Iterate over the tile data to find the current tile type
        for tile_key, tile_data in tiles.items():
            min_x, max_x, min_y, max_y, min_z, max_z = tile_data["size"]

            # Check if the player's position falls within the boundaries of the current tile
            if min_x <= x <= max_x and min_y <= y <= max_y and min_z <= z <= max_z:
                return tile_data["type"]

        # If the player's position doesn't match any tile, return None or a default tile type
        return None

    def get_position(self):
        return self.position

    def get_health(self):
        return self.health

    def get_energy(self):
        return self.energy

    def get_username(self):
        return self.username
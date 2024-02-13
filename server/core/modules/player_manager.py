import math
import base64
import json

class PlayerRegistry:
    _users = {}  # Persistent dictionary of player data
    _instances = {}  # Runtime instances of logged-in players
    
    @classmethod
    def load_users_from_disk(cls, filepath='players.json'):
        try:
            with open(filepath, 'r') as file:
                cls._users = json.load(file)
        except FileNotFoundError:
            cls._users = {}

    @classmethod
    def save_users_to_disk(cls, filepath='players.json'):
        with open(filepath, 'w') as file:
            json.dump(cls._users, file, indent=4)

    @classmethod
    def get_player_basic_info(cls, username):
        if username not in cls._users:
            return None
        player_data = cls._users[username]
        return {
            "username": player_data['username'],
            "current_map": player_data['current_map'],
            "health": player_data['health'],
            "energy": player_data['energy']
        }

    @classmethod
    def register_player(cls, username, player):
        cls._instances[username] = player

    @classmethod
    def deregister_player(cls, username):
        if username in cls._instances:
            cls._users[username] = cls._instances[username].to_dict()
            cls.save_users_to_disk()
            del cls._instances[username]

class Player:
    def __init__(self, username, password, position=(0, 0, 0), current_map="Main", event_dispatcher):
        self.username = username
        self.password = base64.b64encode(password.encode()).decode()  # Consider using a more secure hash function
        self.logged_in = True
        self._position = position
        self.current_map = current_map
        self._yaw = 0
        self._pitch = 0
        self._health = 100
        self._energy = 100
        self.inventory = {}
        self.event_dispatcher = event_dispatcher

        PlayerRegistry.register_player(self.username, self)

    @property
    def health(self):
        return self._health

    @health.setter
    def health(self, value):
        if value < 0:
            self._health = 0
        else:
            self._health = value
        EventDispatcher.dispatch("PlayerHealthChanged", {"username": self.username, "health": self._health})

    @property
    def energy(self):
        return self._energy

    @energy.setter
    def energy(self, value):
        if value < 0:
            self._energy = 0
        else:
            self._energy = value
        EventDispatcher.dispatch("PlayerEnergyChanged", {"username": self.username, "energy": self._energy})

    @property
    def position(self):
        return self._position

    @position.setter
    def position(self, value):
        if not isinstance(value, tuple) or len(value) != 3:
            raise ValueError("Position must be a tuple of three numbers")
        self._position = value
        self.event_dispatcher.dispatch("PlayerMoved", {"username": self.username, "position": self._position})

    @property
    def yaw(self):
        return self._yaw

    @yaw.setter
    def yaw(self, value):
        self._yaw = value % 360
        self.event_dispatcher.dispatch("PlayerTurned", {"username": self.username, "yaw": self._yaw})

    @property
    def pitch(self):
        return self._pitch

    @pitch.setter
    def pitch(self, value):
        self._pitch = max(-90, min(90, value))
        self.event_dispatcher.dispatch("PlayerTurned", {"username": self.username, "pitch": self._pitch})

    def move(self, direction, distance=1):
        x, y, z = self._position  # Corrected to unpack the current position
        dx, dy = 0, 0
        if direction == "forward":
            dx, dy = math.sin(math.radians(self.yaw)), math.cos(math.radians(self.yaw))
        elif direction == "backward":
            dx, dy = -math.sin(math.radians(self.yaw)), -math.cos(math.radians(self.yaw))
        elif direction == "right":
            dx, dy = math.cos(math.radians(self.yaw)), -math.sin(math.radians(self.yaw))
        elif direction == "left":
            dx, dy = -math.cos(math.radians(self.yaw)), math.sin(math.radians(self.yaw))

        new_position = (x + dx * distance, y + dy * distance, z)
        self.position = new_position  # Using the setter

    def turn(self, yaw_delta=0, pitch_delta=0):
        new_yaw = self.yaw + yaw_delta
        self.yaw = new_yaw  # Using the setter

        new_pitch = self.pitch + pitch_delta
        self.pitch = new_pitch  # Using the setter

    def to_dict(self):
        return {
            "username": self.username,
            "password": self.password,
            "logged_in": self.logged_in,
            "position": self.position,
            "current_map": self.current_map,
            "yaw": self.yaw,
            "pitch": self.pitch,
            "health": self.health,
            "energy": self.energy,
            "inventory": self.inventory,
        }

    @classmethod
    def from_dict(cls, data):
        player = cls(data['username'], data['password'], tuple(data['position']), data['current_map'])
        player.health = data.get('health', 100)
        player.energy = data.get('energy', 100)
        player.inventory = data.get('inventory', {})
        player.yaw = data.get('yaw', 0)
        player.pitch = data.get('pitch', 0)
        return player

    @classmethod
    def create_player(cls, username, password, position=(0, 0, 0), current_map="Main"):
        if username in PlayerRegistry._users:
            raise ValueError("Username already exists")
        # Consider hashing the password here for security
        player = cls(username, password, position, current_map)
        PlayerRegistry._users[username] = player.to_dict()
        PlayerRegistry.save_users_to_disk()
        return player

# Initial loading of users from disk
PlayerRegistry.load_users_from_disk()

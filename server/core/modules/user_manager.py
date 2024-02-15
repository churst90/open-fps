import math
import base64
import json
import bcrypt
import asyncio

# project specific imports
from core.events.event_dispatcher import EventDispatcher
from core.data import Data

class UserRegistry:
    _users = {}  # Persistent dictionary of player data
    _instances = {}  # Runtime instances of logged-in players
    _event_dispatcher = EventDispatcher.get_instance()
    _data = Data()

    @classmethod
    async def create_user(cls, username, password):
        # Check if username already exists in the registry
        if username in UserRegistry._users:
            raise ValueError("Username already exists")

        # Create a new player instance using cls to support subclassing
        user = User(cls._event_dispatcher)
        user.set_username(username)
        await user.set_password(password)
        await user.set_health(10000)
        await user.set_energy(10000)
        await user.set_position((0, 0, 0))
        await user.set_pitch(90)
        await user.set_yaw(0)
        # Set other properties as needed
        # Add the user to the registry
        UserRegistry._users[username] = user.to_dict()

    @classmethod
    async def load_users(cls):
        user_data = cls._data.load("users")
        if user_data:
            for name, user_dict in user_data.items():
                cls._instances[name] = User.from_dict(user_dict)
        else:
            print("No user data found or failed to load. Creating the default admin user.")
            await UserRegistry.create_user("admin", "admin")

    @classmethod
    def save_users(cls, filepath='players.json'):
        with open(filepath, 'w') as file:
            json.dump(cls._users, file, indent=4)

    @classmethod
    def register_user(cls, username, player_obj):
        cls._instances[username] = player_obj

    @classmethod
    def deregister_user(cls, username):
        if username in cls._instances:
            cls._users[username] = cls._instances[username].to_dict()
            cls.save_users()
            del cls._instances[username]

class User:
    def __init__(self, event_dispatcher):
        self.username = ""
        self.password = ""
        self.logged_in = True
        self.position = ()
        self.yaw = 0
        self.pitch = 0
        self.health = 0
        self.energy = 0
        self.inventory = {}
        self.event_dispatcher = event_dispatcher

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
        self.set_position(new_position)

    def turn(self, yaw_delta=0, pitch_delta=0):
        new_yaw = self.yaw + yaw_delta
        self.set_yaw(new_yaw)

        new_pitch = self.pitch + pitch_delta
        self.set_pitch(new_pitch)

    # setter methods since you can't use asyncio on decorator setters

    async def set_position(self, position):
        self.position = position
        await self.event_dispatcher.dispatch("PlayerPositionChanged", {"username": self.username, "position": self.position})

    async def set_health(self, health):
        self._health = max(0, health)  # Assuming _health is the correct attribute
        await self.event_dispatcher.dispatch("PlayerHealthChanged", {"username": self.username, "health": self.health})

    async def set_energy(self, energy):
        self.energy = energy
        await self.event_dispatcher.dispatch("PlayerEnergyChanged", {"username": self.username, "energy": self.energy})

    async def set_pitch(self, pitch):
        self.pitch = pitch
        await self.event_dispatcher.dispatch("PlayerPitchChanged", {"username": self.username, "pitch": self.pitch})

    async def set_yaw(self, yaw):
        self.yaw = yaw
        await self.event_dispatcher.dispatch("PlayerYawChanged", {"username": self.username, "yaw": self.yaw})

    def set_username(self, username):
        self.username = username

    async def set_password(self, password):
        hashed_password = await asyncio.to_thread(bcrypt.hashpw, password.encode('utf-8'), bcrypt.gensalt())
        # Store the hashed password rather than the plain one
        self.password = hashed_password.decode('utf-8')

    def to_dict(self):
        return {
            "username": self.username,
            "password": self.password,
            "position": self.position,
            "yaw": self.yaw,
            "pitch": self.pitch,
            "health": self.health,
            "energy": self.energy,
            "inventory": self.inventory,
        }

    @classmethod
    def from_dict(cls, data):
        user_instance = cls(data['username'], data['password'])
        user_instance.health = data['health']
        user_instance.energy = data['energy']
        user_instance.inventory = data['inventory']
        user_instance.yaw = data['yaw']
        user_instance.pitch = data['pitch']
        return user_instance

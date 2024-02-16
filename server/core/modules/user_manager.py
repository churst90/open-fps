import math
import base64
import json
import bcrypt
import asyncio

# project specific imports
from core.events.event_dispatcher import EventDispatcher
from core.data import Data
from .position import Position

class UserRegistry:
    _users = {}  # Persistent dictionary of player data
    _instances = {}  # Runtime instances of logged-in players
    _event_dispatcher = EventDispatcher.get_instance()
    _data = Data()

    @classmethod
    async def create_user(cls, username, password):
        # Check if username already exists in the registry
        if username in cls._users:
            raise ValueError("Username already exists")

        # Create a new player instance using cls to support subclassing
        user = User(cls._event_dispatcher)
        user.update_username(username)
        await user.update_password(password)
        await user.update_health(10000)
        await user.update_energy(10000)
        await user.update_position((0, 0, 0))
        await user.update_pitch(90)
        await user.update_yaw(0)
    
        # Add the user to the registry
        cls._users[username] = user.to_dict()
        cls._instances[username] = user  # Add this line to add the instance

    @classmethod
    async def load_users(cls):
        data = await cls._data.async_init()
        user_data = cls._data.load("users")
        if user_data:
            for name, user_dict in user_data.items():
                cls._instances[name] = User.from_dict(user_dict, cls._event_dispatcher)
                cls._users[name] = user_data
        else:
            print("No user data found or failed to load. Creating the default admin user.")
            await UserRegistry.create_user("admin", "admin")

    @classmethod
    def save_users(cls):
        cls._data.export(cls._users, "users")
        print("Users saved successfully")

    @classmethod
    def register_user(cls, username, player_obj):
        cls._instances[username] = player_obj

    @classmethod
    def deregister_user(cls, username):
        if username in cls._instances:
            cls._users[username] = cls._instances[username].to_dict()
            cls.save_users()
            del cls._instances[username]

    @classmethod
    def get_all_users(self):
        return self._users

class User:
    def __init__(self, event_dispatcher):
        self.username = ""
        self.password = ""
        self.logged_in = True
        self.position = Position()
        self.yaw = 0
        self.pitch = 0
        self.health = 0
        self.energy = 0
        self.inventory = {}
        self.event_dispatcher = event_dispatcher

    async def move(self, direction, distance=1):
        # Calculate new position based on direction and distance
        # Emit 'user_moved' event with new position data
        pass
#    async def turn(self, yaw_delta=0, pitch_delta=0):
        # Update yaw and pitch based on deltas
#        self.event_dispatcher.dispatch({"message_type":"position_update","position":})

    # Add methods for health and energy updates

    async def update_position(self, position):
        pass

    async def update_health(self, change):
        # Update health and emit event
        pass

    async def update_energy(self, change):
        # Update energy and emit event
        pass

    async def update_pitch(self, pitch):
        self.pitch = pitch
        await self.event_dispatcher.dispatch("PlayerPitchChanged", {"username": self.username, "pitch": self.pitch})

    async def update_yaw(self, yaw):
        self.yaw = yaw
        await self.event_dispatcher.dispatch("PlayerYawChanged", {"username": self.username, "yaw": self.yaw})

    def update_username(self, username):
        self.username = username

    async def update_password(self, password):
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
    def from_dict(cls, data, event_dispatcher):
        user_instance = cls(event_dispatcher)
        user_instance.username = data['username']
        user_instance.password = data['username']
        user_instance.health = data['health']
        user_instance.energy = data['energy']
        user_instance.inventory = data['inventory']
        user_instance.yaw = data['yaw']
        user_instance.pitch = data['pitch']
        return user_instance

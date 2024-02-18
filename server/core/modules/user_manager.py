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
    async def login(cls, username, password):
        # first insure the password is hashed if necessary
        if username in _users.keys():
            if password == username[password]:
                UserRegistry.register_user(username)
                # dispatch an event to let other players and maps know the user is online
                return cls._users[username]
            else:
                pass
                # dispatch an event message indicating an incorrect password
        else:
            pass
            # dispatch a message stating the username doesn't exist

    @classmethod
    async def logout(cls, username):
        # Check if the user is actually online before proceeding
        if _user[username][logged_in] == True:
            self.user_reg.deregister_user(username)
            # dispatch an event the user went offline

    @classmethod
    def get_user_instance(cls, username):
        # Check if the username exists in the _instances dictionary
        if username in cls._instances:
            return cls._instances[username]
        else:
            return None

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
        await user.update_pitch(0)
        await user.update_yaw(0)

        # Add the user to the registry
        cls._users[username] = user.to_dict()

    @classmethod
    async def load_users(cls):
        data = await cls._data.async_init()
        print("Attempting to load users.dat from disk ...")
        user_data = cls._data.load("users")
        if user_data:
            print("users.dat loaded successfully")
            # Directly load user data into _users without instantiating User objects
            cls._users = user_data
        else:
            print("Creating the default admin user.")
            await UserRegistry.create_user("admin", "admin")
            print("Default admin user created successfully")

    @classmethod
    def save_users(cls):
        cls._data.export(cls._users, "users")
        print("Users saved successfully")

    @classmethod
    def register_user(cls, username, player_obj):
        if username in cls._users:
            player_obj.update_login_status(True)
            cls._instances[username] = player_obj
            # Immediately reflect this change in _users dictionary
            cls._users[username]['logged_in'] = True
        else:
            print(f"Error: {username} not found for registration.")

    @classmethod
    def deregister_user(cls, username):
        if username in cls._instances:
            # Update the login status to False
            cls._instances[username].update_login_status(False)
            # Save the updated user data
            cls._users[username] = cls._instances[username].to_dict()
            cls.save_users()
        else:
            print(f"Error: {username} not found in instances for deregistration.")

    @classmethod
    def get_all_users(self):
        return self._users

class User:
    def __init__(self, event_dispatcher):
        self.username = ""
        self.password = ""
        self.logged_in = False
        self.position = ()
        self.yaw = 0
        self.pitch = 0
        self.health = 0
        self.energy = 0
        self.inventory = {}
        self.event_dispatcher = event_dispatcher

    async def move(self, direction, distance=1):
        if direction == "forward":
            self.position.move_forward(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  
        elif direction == "backward":
            self.position.move_backward(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  
        elif direction == "left":
            self.position.move_left(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  
        elif direction == "right":
            self.position.move_right(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  
        elif direction == "up":
            self.position.move_up(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  
        elif direction == "down":
            self.position.move_down(distance)
            await self.event_dispatcher.dispatch({"message_type": "update_position", "position": self.position})  

        # compose the event message
        new_position_data = {"type": "position_update", "username": self.username, "new_position": self.position.to_dict()}

#    async def turn(self, yaw_delta=0, pitch_delta=0):
        # Update yaw and pitch based on deltas
#        self.event_dispatcher.dispatch({"message_type":"position_update","position":})

    # Add methods for health and energy updates

    def update_login_status(self, status):
        self.logged_in = status

    async def update_position(self, position):
        self.position = position

    async def update_health(self, health):
        self.health = health

    async def update_energy(self, energy):
        self.energy = energy

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
        user_instance.password = data['password']
        user_instance.health = data['health']
        user_instance.energy = data['energy']
        user_instance.inventory = data['inventory']
        user_instance.yaw = data['yaw']
        user_instance.pitch = data['pitch']
        return user_instance

    def synchronize_with_registry(self):
        # Assuming UserRegistry is accessible and cls._users can be updated
        UserRegistry.update_user_data(self.username, self.to_dict())
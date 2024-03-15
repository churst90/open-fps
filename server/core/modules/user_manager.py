import math
import base64
import json
import bcrypt
import asyncio
from asyncio import Lock
from pathlib import Path
import aiofiles  # Import aiofiles for async file operations

class UserRegistry:
    def __init__(self, event_dispatcher):
        self.instances = {}  # Runtime instances of logged-in players
        self.users_path = Path('users')  # Directory for user files
        self.event_dispatcher = event_dispatcher
        self.user_handler = None

    def set_handler(self, handler):
        self.user_handler = handler

    async def get_user_instance(self, username):
        if username in self.instances:
            return self.instances[username]

    async def create_user(self, username, password):
        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            raise ValueError("Username already exists")

        self.users_path.mkdir(parents=True, exist_ok=True)  # Ensure user directory exists

        # Create and configure the user
        user = User(self.event_dispatcher)
        user.update_username(username)
        await user.update_password(password)

        # Save the new user to disk
        await self.save_user(user)

    async def load_user(self, username):
        user_file_path = self.users_path / f"{username}.usr"
        if user_file_path.exists():
            async with aiofiles.open(user_file_path, 'r') as file:
                user_data = json.loads(await file.read())
                return User.from_dict(user_data, self.event_dispatcher)
        else:
            return None

    async def load_users(self):
        self.users_path.mkdir(parents=True, exist_ok=True)  # Ensure user directory exists

        if not any(self.users_path.iterdir()):  # If the directory is empty, create default admin
            print("No users found, creating default admin user.")
            await self.create_user("admin", "adminpassword")  # Replace "adminpassword" with a secure default password

        for user_file in self.users_path.glob("*.usr"):
            async with aiofiles.open(user_file, 'r') as file:
                user_data = json.loads(await file.read())
                username = user_file.stem
                self.instances[username] = User.from_dict(user_data, self.event_dispatcher)

    async def save_user(self, user_instance):
        user_file_path = self.users_path / f"{user_instance.username}.usr"
        user_data = user_instance.to_dict()
        async with aiofiles.open(user_file_path, 'w') as file:
            await file.write(json.dumps(user_data))
        print(f"User {user_instance.username} saved successfully.")

    async def save_all_users(self):
        for username, user_instance in self.instances.items():
            await self.save_user(user_instance)

    async def register_user(self, username, user_instance):
        if username not in self.instances:
            self.instances[username] = user_instance
            # Emit an event indicating the user has been registered
            await self.event_dispatcher.dispatch("user_registered", {"username": username, "current_map": user_instance.current_map, "user_instance": user_instance})
        else:
            # Handle already registered user
            pass

    async def deregister_user(self, username):
        if username in self.instances:
            user_instance = self.instances[username]
            user_instance.logged_in = False
            # Emit an event indicating the user has been deregistered
            await self.event_dispatcher.dispatch("user_deregistered", {"username": username, "current_map": user_instance.current_map})
            # Save the user's state after deregistering
            await self.save_user(user_instance)
        else:
            # Handle user not found scenario
            pass

    async def get_all_users(self):
        return self.instances

class User:
    def __init__(self, event_dispatcher):
        self.username = ""
        self.password = ""
        self.current_map = ""
        self.current_zone = ""
        self.logged_in = False
        self.position = ()
        self.yaw = 0
        self.pitch = 0
        self.health = 10000
        self.energy = 10000
        self.inventory = {}
        self.event_dispatcher = event_dispatcher

    async def setup_subscriptions(self):
        await self.event_dispatcher.subscribe_client('add_tile', self.username)
        await self.event_dispatcher.subscribe_client('add_zone', self.username)
#        await self.event_dispatcher.subscribe_client('user_moved', self.username)

    def get_position(self):
        return self.position

    def get_current_map(self):
        return self.current_map

    def get_pitch(self):
        return self.pitch

    def get_yaw(self):
        return self.yaw

    def get_current_zone(self):
        return self.current_zone

    def get_health(self):
        return self.health

    def get_energy(self):
        return self.energy

    def update_login_status(self, status):
        self.logged_in = status

    def update_position(self, position):
        self.position = position

    def update_health(self, health):
        self.health = health

    def update_energy(self, energy):
        self.energy = energy

    def update_pitch(self, pitch):
        self.pitch = pitch

    def update_yaw(self, yaw):
        self.yaw = yaw

    def update_username(self, username):
        self.username = username

    async def update_password(self, password):
        hashed_password = await asyncio.to_thread(bcrypt.hashpw, password.encode('utf-8'), bcrypt.gensalt())
        # Store the hashed password rather than the plain one
        self.password = hashed_password.decode('utf-8')

    def update_current_map(self, map):
        self.current_map = map

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
    def from_dict(cls, data, event_dispatcher):
        user_instance = cls(event_dispatcher)
        user_instance.username = data['username']
        user_instance.password = data['password']
        user_instance.current_map =  data['current_map']
        user_instance.logged_in = data['logged_in']
        user_instance.health = data['health']
        user_instance.energy = data['energy']
        user_instance.inventory = data['inventory']
        user_instance.yaw = data['yaw']
        user_instance.pitch = data['pitch']
        return user_instance


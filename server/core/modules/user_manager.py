import math
import base64
import json
import bcrypt
import asyncio
from asyncio import Lock
from pathlib import Path
import aiofiles  # Import aiofiles for async file operations

from core.modules.role_manager import RoleManager
from core.modules.rank_manager import RankManager
from core.modules.achievement_manager import AchievementManager

class UserRegistry:
    def __init__(self, event_dispatcher):
        self.rank_manager = RankManager()
        self.achievement_manager = AchievementManager()
        self.instances = {}  # Runtime instances of logged-in players
        self.users_path = Path('users')  # Directory for user files
        self.event_dispatcher = event_dispatcher
        self.user_handler = None

    def set_handler(self, handler):
        self.user_handler = handler

    async def setup_subscriptions(self):
        await self.event_dispatcher.subscribe_internal("register_user", self.register_user)
        await self.event_dispatcher.subscribe_internal("deregister_user", self.deregister_user)

    async def change_user_role(self, admin_username, target_username, new_role):
        admin_user = self.get_user_instance(admin_username)
        if not admin_user or not admin_user.check_permission('change_role'):
            print("Insufficient permissions to change roles.")
            return

        role_manager = RoleManager.get_instance()
        # Remove from all current roles (optional, depends on how you want to manage roles)
        current_roles = list(role_manager.user_roles.get(target_username, []))
        for role in current_roles:
            role_manager.remove_role_from_user(role, target_username)

        # Assign the new role
        role_manager.assign_role_to_user(new_role, target_username)
        print(f"User {target_username}'s role changed to {new_role} by {admin_username}.")


    async def get_user_instance(self, username):
        if username in self.instances:
            return self.instances[username]

    async def create_user(self, username, password, role = 'player'):
        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            raise ValueError("Username already exists")

        self.users_path.mkdir(parents=True, exist_ok=True)  # Ensure user directory exists

        # Create and configure the user
        user = User(self.event_dispatcher)

        # set the username and password in the new user object instance
        user.set_username(username)
        await user.set_password(password)

        # Assign default role
        role_manager = RoleManager.get_instance()
        role_manager.assign_role_to_user(role, username)

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
            await self.create_user("admin", "adminpassword", role = "developer")

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

    async def register_user(self, event_data):
        # Extract the variables from the event data
        username = event_data['username']
        user_instance = event_data['user_instance']

        if username not in self.instances:
            self.instances[username] = user_instance

            # Emit an event indicating the user has been registered
            await self.event_dispatcher.dispatch("user_registered", {
                "username": username,
                "current_map": user_instance.current_map,
                "user_instance": user_instance
            })

        else:
            # Handle already registered user
            pass

    async def deregister_user(self, event_data):
        # Extract the username from the event data
        username = event_data['username']
        if username in self.instances:
            user_instance = self.instances[username]
            user_instance.logged_in = False

            # Emit an event indicating the user has been deregistered
            await self.event_dispatcher.dispatch("user_deregistered", {
                "username": username,
                "current_map": user_instance.current_map
            })
            # Save the user's state after deregistering
            await self.save_user(user_instance)
        else:
            # Handle user not found scenario
            pass

    # Method that returns the self.instances dictionary for user objects
    async def get_all_users(self):
        return self.instances

# User class for creating individual user objects
class User:
    def __init__(self, event_dispatcher):
        # User instance features and atributes
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
        # other components
        self.event_dispatcher = event_dispatcher

    def has_permission(self, permission):
        role_manager = RoleManager.get_instance()
        return role_manager.has_permission(self.username, permission)

    async def setup_subscriptions(self):
        await self.event_dispatcher.subscribe_client('handle_login', self.username)
        await self.event_dispatcher.subscribe_client('handle_logout', self.username)
        await self.event_dispatcher.subscribe_client('user_registered', self.username)
        await self.event_dispatcher.subscribe_client('user_deregistered', self.username)
        await self.event_dispatcher.subscribe_client('add_tile', self.username)
        await self.event_dispatcher.subscribe_client('remove_tile', self.username)
        await self.event_dispatcher.subscribe_client('add_zone', self.username)
        await self.event_dispatcher.subscribe_client('remove_zone', self.username)
        await self.event_dispatcher.subscribe_client('user_move', self.username)
        await self.event_dispatcher.subscribe_client('user_turn', self.username)
        await self.event_dispatcher.subscribe_client('global_chat', self.username)
        await self.event_dispatcher.subscribe_client('map_chat', self.username)
        await self.event_dispatcher.subscribe_client('private_chat', self.username)

    def get_username(self):
        return self.username

    def get_password(self):
        return self.password

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

    def set_login_status(self, status):
        self.logged_in = status

    def set_position(self, position):
        self.position = position

    def set_health(self, health):
        self.health = health

    def set_energy(self, energy):
        self.energy = energy

    def set_pitch(self, pitch):
        self.pitch = pitch

    def set_yaw(self, yaw):
        self.yaw = yaw

    def set_username(self, username):
        self.username = username

    async def set_password(self, password):
        hashed_password = await asyncio.to_thread(bcrypt.hashpw, password.encode('utf-8'), bcrypt.gensalt())
        # Store the hashed password rather than the plain one
        self.password = hashed_password.decode('utf-8')

    def set_current_map(self, map):
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


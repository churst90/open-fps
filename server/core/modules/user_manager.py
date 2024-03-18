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
        self.lock = Lock()
        self.rank_manager = RankManager()
        self.achievement_manager = AchievementManager()
        self.instances = {}  # Runtime instances of logged-in players
        self.users_path = Path('users')  # Directory for user files
        self.event_dispatcher = event_dispatcher
        self.setup_subscriptions()
        self.users_path = Path('users')

    def setup_subscriptions(self):
        print("user registry subscriptions set up")
        self.event_dispatcher.subscribe_internal("user_account_login_request", self.handle_login)
        self.event_dispatcher.subscribe_internal("user_account_logout_request", self.handle_logout)
        self.event_dispatcher.subscribe_internal("user_account_create_request", self.create_user)
        self.event_dispatcher.subscribe_internal("user_account_delete_request", self.delete_user)

    async def create_user(self, event_data):
        print("create user method called")
        username = event_data['username']
        password = event_data['password']
        role = event_data['role']
        print("Event data stored in local variables")

        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            print("That user name already exists")
            raise ValueError("Username already exists")

        # Make sure the users directory exists
        self.users_path.mkdir(parents=True, exist_ok=True)  # Ensure user directory exists

        # Create and configure the user
        user = User(self.event_dispatcher)
        print("Created the user instance")

        # set the username and password in the new user object instance
        user.set_username(username)
        await user.set_password(password)
        print("Stored the username and password in the user instance")

        role_manager = RoleManager.get_instance()
        role_manager.assign_role_to_user(role, username)
        print("Retrieved the role instance and assigned the developer role to the default admin account")

        # Save the new user to disk
        await self.save_user(user)
        print("user saved to disk")

    async def delete_user(self, username):
        pass

    async def handle_login(self, event_data):
        username = event_data['username']
        password = event_data['password']

        user = await self.load_user(username)
        if user is None:
            await self.event_dispatcher.dispatch("user_account_login_failed", {
                "message_type": "login_failed",
                "username": username,
                "message": "User does not exist"
            })
            return

            stored_hashed_password = user.get_password()

            # compare and validate passwords
            stored_hashed_password = user.get_password().encode('utf-8')
            if bcrypt.checkpw(password.encode('utf-8'), stored_hashed_password):
                user.set_login_status(True)

                await self.event_dispatcher.dispatch("user_account_login_ok", {
                    "username": username,
                    "user_instance": user
                })
        else:
            await self.event_dispatcher.dispatch("user_account_login_failed", {
                "message_type": "login_failed",
                "username": username,
                "message": "invalid credentials"
            })

    async def handle_logout(self, event_data):
        username = event_data['username']

        # Perform logout logic, e.g., marking the user as logged out
        user = await self.get_user_instance(username)

        if user:
            user.set_login_status(False)

            # dispatch a message for the map listener to see and add the user to their map
            await self.event_dispatcher.dispatch("user_account_logout_ok", {
                "username": username,
                "user_instance": user
            })
        else:
            await self.event_dispatcher.dispatch("user_account_logout_failed", {
                "message_type": "logout_failed",
                "username": username,
                "message": "User not found"
            })

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

    async def load_user(self, username):
        user_file_path = self.users_path / f"{username}.usr"
        if user_file_path.exists():
            async with aiofiles.open(user_file_path, 'r') as file:
                user_data = json.loads(await file.read())
                return User.from_dict(user_data)
        else:
            return None

    async def save_user(self, user_instance):
        user_file_path = self.users_path / f"{user_instance.username}.usr"
        user_data = user_instance.to_dict()
        async with aiofiles.open(user_file_path, 'w') as file:
            await file.write(json.dumps(user_data))
        print(f"User {user_instance.username} saved successfully.")

    async def save_all_users(self):
        for username, user_instance in self.instances.items():
            await self.save_user(user_instance)

    # Method that returns the self.instances dictionary for user objects
    async def get_user_instance(self, username):
        if username in self.instances:
            return self.instances[username]

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


        self.event_dispatcher = event_dispatcher
        self.setup_subscriptions()

    def has_permission(self, permission):
        print("user has permission method called")
        role_manager = RoleManager.get_instance()
        return role_manager.has_permission(self.username, permission)

    def setup_subscriptions(self):
        print("setting up user instance subscriptions")
        self.event_dispatcher.subscribe_client('handle_login', self.username)
        self.event_dispatcher.subscribe_client('handle_logout', self.username)
        self.event_dispatcher.subscribe_client('user_registered', self.username)
        self.event_dispatcher.subscribe_client('user_deregistered', self.username)
        self.event_dispatcher.subscribe_client('add_tile', self.username)
        self.event_dispatcher.subscribe_client('remove_tile', self.username)
        self.event_dispatcher.subscribe_client('add_zone', self.username)
        self.event_dispatcher.subscribe_client('remove_zone', self.username)
        self.event_dispatcher.subscribe_client('user_move', self.username)
        self.event_dispatcher.subscribe_client('user_turn', self.username)
        self.event_dispatcher.subscribe_client('global_chat', self.username)
        self.event_dispatcher.subscribe_client('map_chat', self.username)
        self.event_dispatcher.subscribe_client('private_chat', self.username)

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


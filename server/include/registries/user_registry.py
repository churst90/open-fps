import math
import base64
import json
import bcrypt
import asyncio
from asyncio import Lock
from pathlib import Path
import aiofiles  # Import aiofiles for async file operations

from include.managers.role_manager import RoleManager
from include.managers.rank_manager import RankManager
from include.managers.achievement_manager import AchievementManager
from include.assets.user import User

class UserRegistry:
    def __init__(self):
        self.lock = Lock()
        self.rank_manager = RankManager()
        self.achievement_manager = AchievementManager()
        self.instances = {}  # Runtime instances of logged-in players
        self.users_path = Path('users')

    async def create_user(self, event_data):
        username = event_data['username']
        password = event_data['password']
        role = event_data['role']

        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            print("That user name already exists")
            raise ValueError("Username already exists")

        # Make sure the users directory exists
        self.users_path.mkdir(parents=True, exist_ok=True)  # Ensure user directory exists

        # Create and configure the user
        user = User()

        # set the username and password in the new user object instance
        user.set_username(username)
        await user.set_password(password)

        role_manager = RoleManager.get_instance()
        role_manager.assign_role_to_user(role, username)

        # Save the new user to disk
        await self.save_user(user)

    async def remove_user(self, username):
        pass

    async def login(self, event_data):
        username = event_data['username']
        password = event_data['password']

        user = await self.load_user(username)
        if user is None:
            return False

            stored_hashed_password = user.get_password()

            # compare and validate passwords
            stored_hashed_password = user.get_password().encode('utf-8')
            if bcrypt.checkpw(password.encode('utf-8'), stored_hashed_password):
                user.set_login_status(True)

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
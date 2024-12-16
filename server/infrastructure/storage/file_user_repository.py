# infrastructure/storage/file_user_repository.py
import asyncio
import json
from pathlib import Path
import aiofiles
import logging
from typing import Optional

from infrastructure.logging.custom_logger import get_logger
from domain.users.user import User  # Adjust path if your User class is elsewhere
# If you have a user_repository_interface.py, ensure it’s implemented and we can import it if needed.
# Assuming we are just directly using this repo.

class FileUserRepository:
    def __init__(self, logger=None, users_dir='users_data'):
        self.lock = asyncio.Lock()
        self.users_path = Path(users_dir)
        self.logger = logger or get_logger('user_registry', debug_mode=False)
        self.users_path.mkdir(parents=True, exist_ok=True)

    async def create_account(self, event_data):
        username = event_data['username']
        password = event_data['password']
        role = event_data['role']
        current_map = event_data['current_map']

        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            self.logger.warning(f"User creation failed: Username '{username}' already exists.")
            return False

        user = User(username, password, current_map=current_map)
        await user.set_password(password)
        user.role = role
        await self.save_user(user)
        self.logger.info(f"Account created successfully for username: {username}")
        return True

    async def authenticate_user(self, username, password):
        user = await self.load_user(username)
        if user and user.check_password(password):
            user.logged_in = True
            await self.save_user(user)
            self.logger.info(f"User '{username}' authenticated successfully.")
            return user.to_dict()
        self.logger.warning(f"Authentication failed for username: {username}")
        return None

    async def deauthenticate_user(self, username):
        async with self.lock:
            user = await self.load_user(username)
            if user:
                user.logged_in = False
                await self.save_user(user)
                self.logger.info(f"User '{username}' deauthenticated successfully.")
                return True
        self.logger.warning(f"Deauthentication failed: User '{username}' not found.")
        return False

    async def load_user(self, username):
        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            try:
                async with aiofiles.open(user_file, 'r') as f:
                    user_data = json.loads(await f.read())
                return User.from_dict(user_data)
            except Exception as e:
                self.logger.exception(f"Error loading user '{username}': {e}")
        else:
            self.logger.debug(f"User file for '{username}' does not exist.")
        return None

    async def save_user(self, user):
        user_file = self.users_path / f"{user.username}.usr"
        try:
            async with aiofiles.open(user_file, 'w') as f:
                await f.write(json.dumps(user.to_dict()))
            self.logger.debug(f"User '{user.username}' saved successfully.")
        except Exception as e:
            self.logger.exception(f"Error saving user '{user.username}': {e}")

    async def save_all_users(self):
        async with self.lock:
            usernames = await self.get_all_usernames()
            for username in usernames:
                user = await self.load_user(username)
                if user:
                    await self.save_user(user)
            self.logger.info("All users saved successfully.")

    async def get_all_usernames(self) -> list:
        try:
            usernames = [f.stem for f in self.users_path.glob("*.usr")]
            self.logger.debug(f"Retrieved all usernames: {usernames}")
            return usernames
        except Exception as e:
            self.logger.exception("Failed to retrieve usernames.")
            return []

    async def get_logged_in_usernames(self) -> list:
        try:
            logged_in_users = []
            usernames = await self.get_all_usernames()
            for username in usernames:
                user = await self.load_user(username)
                if user and user.logged_in:
                    logged_in_users.append(username)
            self.logger.debug(f"Retrieved logged-in usernames: {logged_in_users}")
            return logged_in_users
        except Exception as e:
            self.logger.exception("Failed to retrieve logged-in usernames.")
            return []

    async def get_users_in_map(self, map_name: str) -> list:
        users_in_map = []
        for username in await self.get_all_usernames():
            user = await self.load_user(username)
            if user and user.current_map == map_name:
                users_in_map.append(user)
        return users_in_map

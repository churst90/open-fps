import asyncio
import bcrypt
import json
from pathlib import Path
import aiofiles
from include.custom_logger import get_logger
from include.assets.user import User

class UserRegistry:
    def __init__(self, logger=None):
        self.users = {}  # Cache of user instances
        self.lock = asyncio.Lock()
        self.users_path = Path('users')
        self.logger = logger or get_logger('user_registry', debug_mode=True)

    def get_all_usernames(self):
        """Return a list of all usernames currently loaded in memory."""
        try:
            usernames = list(self.users.keys())
            self.logger.info(f"Retrieved list of all usernames: {usernames}")
            return usernames
        except Exception as e:
            self.logger.exception("Failed to retrieve usernames.")
            return []

    def get_logged_in_usernames(self):
        """Return a list of usernames for currently logged-in users."""
        try:
            logged_in_users = [username for username, user in self.users.items() if user.logged_in]
            self.logger.info(f"Retrieved list of logged-in users: {logged_in_users}")
            return logged_in_users
        except Exception as e:
            self.logger.exception("Failed to retrieve logged-in usernames.")
            return []

    async def create_account(self, event_data):
        """Create a new user account."""
        username = event_data['username']
        password = event_data['password']
        role = event_data['role']
    
        user_file = self.users_path / f"{username}.usr"

        # Check if the user already exists
        if user_file.exists():
            self.logger.warning(f"User creation failed: Username '{username}' already exists.")
            return False

        # Ensure the users directory exists
        self.users_path.mkdir(parents=True, exist_ok=True)

        try:
            # Create a new User instance and set its properties
            user = User()  # Create an empty user instance
            user.username = username  # Use the property setter
            await user.set_password(password)
            user.current_map = "Main"
            user.role = role  # Assign the role

            # Save the user to the file system
            await self.save_user(user)
            self.logger.info(f"Account created successfully for username: {username}")
            return True
        except Exception as e:
            self.logger.exception(f"Error while creating account for username '{username}': {e}")
            return False

    async def authenticate_user(self, username, password):
        """Authenticate a user by username and password."""
        try:
            user = await self.load_user(username)
            if user and await user.check_password(password):
                self.users[username] = user
                self.logger.info(f"User '{username}' authenticated successfully.")
                return user.to_dict()
            self.logger.warning(f"Authentication failed for username: {username}")
        except Exception as e:
            self.logger.exception(f"Error authenticating user '{username}': {e}")
        return None

    async def deauthenticate_user(self, username):
        """Log out and deauthenticate a user."""
        async with self.lock:
            if username in self.users:
                user = self.users[username]
                user.logged_in = False
                await self.save_user(user)
                del self.users[username]
                self.logger.info(f"User '{username}' deauthenticated successfully.")
                return True
        self.logger.warning(f"Deauthentication failed: User '{username}' not found.")
        return False

    async def load_user(self, username):
        """Load a user from the file system."""
        user_file = self.users_path / f"{username}.usr"
        if user_file.exists():
            try:
                async with aiofiles.open(user_file, 'r') as f:
                    user_data = json.loads(await f.read())
                    self.logger.debug(f"User '{username}' loaded successfully.")
                    return User.from_dict(user_data)
            except Exception as e:
                self.logger.exception(f"Error loading user '{username}': {e}")
        else:
            self.logger.warning(f"User file for '{username}' does not exist.")
        return None

    async def save_user(self, user):
        """Save a user to the file system."""
        user_file = self.users_path / f"{user.username}.usr"
        try:
            async with aiofiles.open(user_file, 'w') as f:
                await f.write(json.dumps(user.to_dict()))
            self.logger.debug(f"User '{user.username}' saved successfully.")
        except Exception as e:
            self.logger.exception(f"Error saving user '{user.username}': {e}")

    async def save_all_users(self):
        """Save all users currently in memory to disk."""
        async with self.lock:
            for user in self.users.values():
                await self.save_user(user)
            self.logger.info("All users in memory saved successfully.")

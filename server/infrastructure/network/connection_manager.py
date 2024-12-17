# infrastructure/network/connection_manager.py
import asyncio
import logging
from typing import Optional
from infrastructure.logging.custom_logger import get_logger

class ConnectionManager:
    """
    ConnectionManager manages mappings between usernames and client_ids:
     - username_to_client[username] = client_id
     - client_to_username[client_id] = username

    Allows quick lookups for sending messages to a user or cleaning up on disconnect.

    One session per username is enforced. If multiple sessions per user are needed,
    the internal structure can be adjusted to maintain lists rather than single entries.
    """

    def __init__(self, logger: Optional[logging.Logger] = None):
        # If no logger is provided, create a default one.
        self.logger = logger or get_logger("ConnectionManager", debug_mode=False)
        self._username_to_client = {}
        self._client_to_username = {}
        self._lock = asyncio.Lock()

    async def register_login(self, username: str, client_id: str):
        """
        Called when a user logs in. Updates mappings, removing old ones if they exist.
        """
        async with self._lock:
            old_client_id = self._username_to_client.get(username)
            if old_client_id:
                # If user was previously connected from another client, remove that mapping.
                self.logger.debug(f"Re-mapping '{username}' from old_client_id='{old_client_id}' to new_client_id='{client_id}'.")
                self._client_to_username.pop(old_client_id, None)

            self._username_to_client[username] = client_id
            self._client_to_username[client_id] = username
            self.logger.info(f"User '{username}' logged in and mapped to client_id='{client_id}'.")

    async def register_logout(self, username: str):
        """
        Called when a user logs out. Removes username's mapping if it exists.
        """
        async with self._lock:
            client_id = self._username_to_client.pop(username, None)
            if client_id:
                self._client_to_username.pop(client_id, None)
                self.logger.info(f"User '{username}' logged out and mapping removed (client_id='{client_id}').")
            else:
                self.logger.debug(f"Logout requested for '{username}', but no active session found.")

    async def handle_disconnect(self, client_id: str):
        """
        Called when a client disconnects unexpectedly or gracefully.
        Removes any associated username mapping.
        """
        async with self._lock:
            username = self._client_to_username.pop(client_id, None)
            if username:
                self._username_to_client.pop(username, None)
                self.logger.info(f"Client '{client_id}' disconnected, unmapped from username='{username}'.")
            else:
                self.logger.debug(f"Disconnect event for client_id='{client_id}' with no username mapping found.")

    async def get_client_id_by_username(self, username: str) -> Optional[str]:
        """
        Returns the client_id for the given username, or None if not logged in.
        """
        async with self._lock:
            cid = self._username_to_client.get(username)
            self.logger.debug(f"get_client_id_by_username('{username}') -> '{cid}'")
            return cid

    async def get_username_by_client_id(self, client_id: str) -> Optional[str]:
        """
        Returns the username for the given client_id, or None if not found.
        """
        async with self._lock:
            uname = self._client_to_username.get(client_id)
            self.logger.debug(f"get_username_by_client_id('{client_id}') -> '{uname}'")
            return uname

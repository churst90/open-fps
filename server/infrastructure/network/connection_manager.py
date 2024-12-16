# infrastructure/network/connection_manager.py
import asyncio
import logging
from typing import Optional

class ConnectionManager:
    """
    ConnectionManager manages the mapping between usernames and client_ids.
    It allows us to quickly find which client_id corresponds to a given username
    (to send messages to a specific user), and also find the username for a given
    client_id (e.g., to clean up on disconnect).

    - username_to_client: Dict[username, client_id]
    - client_to_username: Dict[client_id, username]

    This assumes one session per username at a time. If multiple sessions per username
    are desired, this can be adjusted to store lists of client_ids per username.
    """

    def __init__(self, logger: Optional[logging.Logger] = None):
        self.logger = logger or logging.getLogger("ConnectionManager")
        self._username_to_client = {}
        self._client_to_username = {}
        self._lock = asyncio.Lock()

    async def register_login(self, username: str, client_id: str):
        """
        Called when a user successfully logs in and a client_id is associated with them.
        If the username was previously logged in with another client_id, we overwrite it.
        This enforces one active session per username.
        """
        async with self._lock:
            # If username was previously connected, remove old mappings
            old_client_id = self._username_to_client.get(username)
            if old_client_id:
                self.logger.debug(f"Username '{username}' was previously mapped to '{old_client_id}', removing old mapping.")
                self._client_to_username.pop(old_client_id, None)

            self._username_to_client[username] = client_id
            self._client_to_username[client_id] = username
            self.logger.info(f"Registered login: username='{username}', client_id='{client_id}'")

    async def register_logout(self, username: str):
        """
        Called when a user logs out. Removes any mapping for that username.
        The corresponding client_id is also removed. If username is not found,
        this is a no-op.
        """
        async with self._lock:
            client_id = self._username_to_client.pop(username, None)
            if client_id:
                self._client_to_username.pop(client_id, None)
                self.logger.info(f"Registered logout: username='{username}', client_id='{client_id}'")
            else:
                self.logger.debug(f"No active session found for username='{username}' on logout.")

    async def handle_disconnect(self, client_id: str):
        """
        Called when a client disconnects. If that client was associated with a username,
        remove that mapping.
        """
        async with self._lock:
            username = self._client_to_username.pop(client_id, None)
            if username:
                self._username_to_client.pop(username, None)
                self.logger.info(f"Handle disconnect: client_id='{client_id}' was associated with '{username}', mapping removed.")
            else:
                self.logger.debug(f"Client_id='{client_id}' disconnected, but no username mapping found.")

    async def get_client_id_by_username(self, username: str) -> Optional[str]:
        """
        Return the client_id associated with a given username, or None if not found.
        """
        async with self._lock:
            return self._username_to_client.get(username)

    async def get_username_by_client_id(self, client_id: str) -> Optional[str]:
        """
        Return the username associated with a given client_id, or None if not found.
        """
        async with self._lock:
            return self._client_to_username.get(client_id)

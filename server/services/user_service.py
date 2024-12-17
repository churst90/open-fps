# services/user_service.py
import logging
from typing import Optional

from interfaces.event_dispatcher import EventDispatcher
from domain.users.user import User
from infrastructure.storage.user_repository_interface import UserRepositoryInterface
from services.map_service import MapService
from infrastructure.network.connection_manager import ConnectionManager
from infrastructure.security.security_manager import SecurityManager
from infrastructure.logging.custom_logger import get_logger

class UserService:
    def __init__(
        self,
        event_dispatcher: EventDispatcher,
        user_repository: UserRepositoryInterface,
        map_service: MapService,
        connection_manager: ConnectionManager,
        security_manager: SecurityManager,
        logger: Optional[logging.Logger] = None
    ):
        self.event_dispatcher = event_dispatcher
        self.user_repository = user_repository
        self.map_service = map_service
        self.connection_manager = connection_manager
        self.security_manager = security_manager
        self.logger = logger or get_logger("UserService", debug_mode=False)
        self.logger.debug("UserService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("user_account_create_request", self.handle_create_account)
        await self.event_dispatcher.subscribe("user_account_login_request", self.handle_login_request)
        await self.event_dispatcher.subscribe("user_account_logout_request", self.handle_logout_request)
        self.logger.info("UserService subscribed to user account events.")

    async def handle_create_account(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        username = msg.get("username")
        password = msg.get("password")
        role = msg.get("role", "player")
        current_map = msg.get("current_map", "Main")

        if not username or not password:
            self.logger.warning("Create account request missing username or password.")
            await self._fail("user_account_create_fail", client_id, "Missing username or password.")
            return

        self.logger.debug(f"Attempting to create account for username='{username}', role='{role}', map='{current_map}'.")

        success = await self.user_repository.create_account({
            "username": username,
            "password": password,
            "role": role,
            "current_map": current_map
        })

        if success:
            self.logger.info(f"Account created successfully for username='{username}'.")
            await self._ok("user_account_create_ok", client_id, {"username": username, "role": role})

            # Assign role to user
            from infrastructure.storage.role_manager import RoleManager
            role_mgr = RoleManager.get_instance()
            role_mgr.assign_role_to_user(role, username)
            self.logger.debug(f"Assigned role '{role}' to user '{username}'.")
        else:
            self.logger.warning(f"Failed to create account for username='{username}'. User may already exist.")
            await self._fail("user_account_create_fail", client_id, "Could not create account.")

    async def handle_login_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        username = msg.get("username")
        password = msg.get("password")

        self.logger.debug(f"Login request for username='{username}', client_id='{client_id}'.")

        if not username or not password:
            self.logger.warning("Login request missing username or password.")
            await self._fail("user_account_login_fail", client_id, "Missing username or password.")
            return

        user_data = await self.user_repository.authenticate_user(username, password)
        if user_data:
            self.logger.info(f"User '{username}' authenticated successfully.")

            # Ensure user map placement
            user = await self.user_repository.load_user(username)
            if user:
                if not user.current_map or user.current_map.strip() == "":
                    self.logger.debug(f"User '{username}' has no current_map assigned, placing on 'Main'.")
                    main_map = await self.map_service.map_repository.load_map("Main")
                    if main_map:
                        user.current_map = "Main"
                        user.position = main_map.start_position
                        self.logger.debug(f"Main map loaded, assigning start position {main_map.start_position} to user '{username}'.")
                    else:
                        user.current_map = "Main"
                        user.position = (0.0,0.0,0.0)
                        self.logger.debug("Main map not found, using fallback position (0,0,0).")
                    await self.user_repository.save_user(user)

            # Create JWT token
            token = self.security_manager.create_token(username)
            self.logger.info(f"User '{username}' logged in and token issued.")

            # Map username to client_id
            await self.connection_manager.register_login(username, client_id)
            self.logger.debug(f"Username '{username}' mapped to client_id='{client_id}'.")

            await self._ok("user_account_login_ok", client_id, {"username": username, "token": token})
        else:
            self.logger.warning(f"Invalid login attempt for username='{username}'.")
            await self._fail("user_account_login_fail", client_id, "Invalid username or password.")

    async def handle_logout_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")

        self.logger.debug(f"Logout request from client_id='{client_id}', username='{username}'.")

        # If username was provided, we could unmap it. Otherwise just return success.
        if username:
            await self.connection_manager.register_logout(username)
            self.logger.info(f"User '{username}' logged out and mapping removed.")
        else:
            self.logger.debug("No username provided in logout request.")

        await self._ok("user_account_logout_ok", client_id, {"message": "Logout successful. Discard the token."})

    def is_authenticated(self, username: str, token: str) -> bool:
        authed = self.security_manager.is_authenticated(username, token)
        if authed:
            self.logger.debug(f"User '{username}' token validated successfully.")
        else:
            self.logger.debug(f"User '{username}' token validation failed.")
        return authed

    async def get_logged_in_usernames(self) -> list:
        usernames = await self.user_repository.get_logged_in_usernames()
        self.logger.debug(f"get_logged_in_usernames returned {len(usernames)} users: {usernames}")
        return usernames

    def get_client_id_by_username(self, username: str) -> Optional[str]:
        # Return client_id without async lock because we just do a direct look-up
        return self.connection_manager._username_to_client.get(username)  # Accessing internals for example, or implement a getter

    async def _ok(self, event_type: str, client_id: str, data: dict):
        self.logger.debug(f"Dispatching OK event '{event_type}' to client_id='{client_id}' with data={data}")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        self.logger.debug(f"Dispatching FAIL event '{event_type}' to client_id='{client_id}' reason='{reason}'")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

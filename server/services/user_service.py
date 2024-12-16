# services/user_service.py
import logging
from typing import Optional

from interfaces.event_dispatcher import EventDispatcher
from domain.users.user import User
from infrastructure.storage.user_repository_interface import UserRepositoryInterface
from services.map_service import MapService
from infrastructure.network.connection_manager import ConnectionManager
from infrastructure.security.security_manager import SecurityManager

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
        self.logger = logger or logging.getLogger("UserService")

    async def start(self):
        await self.event_dispatcher.subscribe("user_account_create_request", self.handle_create_account)
        await self.event_dispatcher.subscribe("user_account_login_request", self.handle_login_request)
        await self.event_dispatcher.subscribe("user_account_logout_request", self.handle_logout_request)

    async def handle_create_account(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        username = msg["username"]
        password = msg["password"]
        role = msg.get("role", "player")
        current_map = msg.get("current_map", "Main")

        success = await self.user_repository.create_account({
            "username": username,
            "password": password,
            "role": role,
            "current_map": current_map
        })

        if success:
            self.logger.info(f"Account created for username: {username}")
            await self._ok("user_account_create_ok", client_id, {"username": username, "role": role})
            from infrastructure.storage.role_manager import RoleManager
            role_mgr = RoleManager.get_instance()
            role_mgr.assign_role_to_user(role, username)
        else:
            await self._fail("user_account_create_fail", client_id, "Could not create account.")

    async def handle_login_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        username = msg["username"]
        password = msg["password"]

        user_data = await self.user_repository.authenticate_user(username, password)
        if user_data:
            # Ensure user map placement
            user = await self.user_repository.load_user(username)
            if user:
                if not user.current_map or user.current_map.strip() == "":
                    main_map = await self.map_service.map_repository.load_map("Main")
                    if main_map:
                        user.current_map = "Main"
                        user.position = main_map.start_position
                    else:
                        user.current_map = "Main"
                        user.position = (0,0,0)
                    await self.user_repository.save_user(user)

            # Create JWT token via security_manager
            token = self.security_manager.create_token(username)
            self.logger.info(f"User '{username}' logged in. Token issued.")
            # Store the mapping from username to client_id in connection_manager
            self.connection_manager.map_username_to_client_id(username, client_id)

            await self._ok("user_account_login_ok", client_id, {"username": username, "token": token})
        else:
            await self._fail("user_account_login_fail", client_id, "Invalid username or password.")

    async def handle_logout_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        # No token revocation implemented. Just advise client to discard token.
        # If username and token were provided, we could remove their mapping.
        # For now, we just respond success.
        await self._ok("user_account_logout_ok", client_id, {"message": "Logout successful. Discard the token."})

    def is_authenticated(self, username: str, token: str) -> bool:
        return self.security_manager.is_authenticated(username, token)

    async def get_logged_in_usernames(self) -> list:
        return await self.user_repository.get_logged_in_usernames()

    def get_client_id_by_username(self, username: str) -> Optional[str]:
        return self.connection_manager.get_client_id_for_username(username)

    async def _ok(self, event_type: str, client_id: str, data: dict):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

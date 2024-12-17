# services/role_service.py
import logging
from typing import Optional
from infrastructure.logging.custom_logger import get_logger

class RoleService:
    """
    The RoleService handles assigning, removing, and checking roles for users.
    It uses RoleManager to manage the underlying role and permission data.

    Events:
    - "role_assign_request"
    - "role_remove_request"

    The service updates roles accordingly and dispatches success/failure events.
    """

    def __init__(self, event_dispatcher, role_manager, logger: Optional[logging.Logger] = None):
        """
        :param event_dispatcher: EventDispatcher for subscribing/dispatching events.
        :param role_manager: Manages roles and permissions.
        :param logger: Optional logger, else one is created.
        """
        self.event_dispatcher = event_dispatcher
        self.role_manager = role_manager
        self.logger = logger or get_logger("RoleService", debug_mode=False)
        self.logger.debug("RoleService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("role_assign_request", self.handle_role_assign_request)
        await self.event_dispatcher.subscribe("role_remove_request", self.handle_role_remove_request)
        self.logger.info("RoleService subscribed to role-related events.")

    async def handle_role_assign_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        role_name = msg.get("role_name")

        self.logger.debug(f"Handling role_assign_request from client_id='{client_id}' for username='{username}', role='{role_name}'.")

        if not username or not role_name:
            reason = "Missing username or role_name."
            self.logger.warning(f"role_assign_request failed: {reason}")
            await self._fail("role_assign_fail", client_id, reason)
            return

        available_roles = self.role_manager.list_roles()
        self.logger.debug(f"Available roles: {available_roles}")
        if role_name not in available_roles:
            reason = f"Role '{role_name}' does not exist."
            self.logger.warning(f"role_assign_request failed: {reason}")
            await self._fail("role_assign_fail", client_id, reason)
            return

        self.role_manager.assign_role_to_user(role_name, username)
        self.logger.info(f"Assigned role '{role_name}' to user '{username}'.")
        await self._ok("role_assign_ok", client_id, {"username": username, "role": role_name})

    async def handle_role_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")

        self.logger.debug(f"Handling role_remove_request from client_id='{client_id}' for username='{username}'.")

        if not username:
            reason = "Missing username."
            self.logger.warning(f"role_remove_request failed: {reason}")
            await self._fail("role_remove_fail", client_id, reason)
            return

        if not self.role_manager.user_has_role(username):
            reason = f"User '{username}' has no role assigned."
            self.logger.warning(f"role_remove_request failed: {reason}")
            await self._fail("role_remove_fail", client_id, reason)
            return

        self.role_manager.remove_role_from_user(username)
        self.logger.info(f"Removed role from user '{username}'.")
        await self._ok("role_remove_ok", client_id, {"username": username})

    async def _ok(self, event_type: str, client_id: str, data: dict):
        self.logger.debug(f"Dispatching OK event '{event_type}' for client_id='{client_id}' with data={data}")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        self.logger.debug(f"Dispatching FAIL event '{event_type}' for client_id='{client_id}', reason='{reason}'")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

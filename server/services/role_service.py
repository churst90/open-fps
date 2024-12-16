# services/role_service.py
import logging
from typing import Optional

class RoleService:
    """
    The RoleService is responsible for assigning, removing, and checking roles 
    for users. It interacts with a RoleManager (or RoleRepository) that stores 
    and retrieves role information.

    Potential events:
    - "role_assign_request"
    - "role_remove_request"
    - "role_check_request"

    The service listens for these events, updates roles accordingly, and dispatches 
    success/failure events. It can also provide utility methods to other services 
    (though ideally they just dispatch an event and wait for the response).
    """

    def __init__(self, event_dispatcher, role_manager, logger: Optional[logging.Logger] = None):
        """
        :param event_dispatcher: For subscribing and dispatching events.
        :param role_manager: Manages roles and permissions.
        :param logger: Optional logger.
        """
        self.event_dispatcher = event_dispatcher
        self.role_manager = role_manager
        self.logger = logger or logging.getLogger("RoleService")

    async def start(self):
        # Subscribe to events related to role management
        await self.event_dispatcher.subscribe("role_assign_request", self.handle_role_assign_request)
        await self.event_dispatcher.subscribe("role_remove_request", self.handle_role_remove_request)
        # "role_check_request" could be implemented if needed

    async def handle_role_assign_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "role_assign_request",
            "username": <str>,
            "role_name": <str>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        role_name = msg.get("role_name")

        if not username or not role_name:
            await self._fail("role_assign_fail", client_id, "Missing username or role_name.")
            return

        if role_name not in self.role_manager.list_roles():
            await self._fail("role_assign_fail", client_id, f"Role '{role_name}' does not exist.")
            return

        # Assign the role
        self.role_manager.assign_role_to_user(role_name, username)
        self.logger.info(f"Assigned role '{role_name}' to user '{username}'.")
        await self._ok("role_assign_ok", client_id, {"username": username, "role": role_name})

    async def handle_role_remove_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "role_remove_request",
            "username": <str>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")

        if not username:
            await self._fail("role_remove_fail", client_id, "Missing username.")
            return

        if not self.role_manager.user_has_role(username):
            await self._fail("role_remove_fail", client_id, f"User '{username}' has no role assigned.")
            return

        self.role_manager.remove_role_from_user(username)
        self.logger.info(f"Removed role from user '{username}'.")
        await self._ok("role_remove_ok", client_id, {"username": username})

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

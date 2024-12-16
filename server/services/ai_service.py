# services/ai_service.py
import logging
from typing import Optional
from domain.ai.ai_entity import AIEntity
import uuid

class AIService:
    """
    AIService handles AI-related actions:
    - Spawning new AI entities
    - Removing AI entities
    - Moving AI entities
    - Updating AI health or other attributes

    Events:
      - "ai_spawn_request" -> creates an AI
      - "ai_remove_request" -> removes an AI
      - "ai_move_request" -> moves an AI
      - "ai_update_health_request" -> updates an AI's health

    Dispatches:
      - "ai_spawn_ok" / "ai_spawn_fail"
      - "ai_remove_ok" / "ai_remove_fail"
      - "ai_move_ok" / "ai_move_fail"
      - "ai_update_health_ok" / "ai_update_health_fail"
    """

    def __init__(self, event_dispatcher, ai_repository, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.ai_repository = ai_repository
        self.logger = logger or logging.getLogger("AIService")

    async def start(self):
        await self.event_dispatcher.subscribe("ai_spawn_request", self.handle_ai_spawn_request)
        await self.event_dispatcher.subscribe("ai_remove_request", self.handle_ai_remove_request)
        await self.event_dispatcher.subscribe("ai_move_request", self.handle_ai_move_request)
        await self.event_dispatcher.subscribe("ai_update_health_request", self.handle_ai_update_health_request)

    async def handle_ai_spawn_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "ai_spawn_request",
            "name": <str>,
            "position": [x, y, z],
            "health": <int>,
            "speed": <float>,
            "role": <str, optional>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        name = msg.get("name")
        position = msg.get("position")
        health = msg.get("health", 100)
        speed = msg.get("speed", 1.0)
        role = msg.get("role", "npc")

        if not name or not position:
            await self._fail("ai_spawn_fail", client_id, "Missing name or position.")
            return

        ai_id = str(uuid.uuid4())
        ai_entity = AIEntity(ai_id, name, tuple(position), health, speed, role)

        success = await self.ai_repository.save_ai(ai_entity)
        if success:
            self.logger.info(f"AI '{name}' spawned with ID {ai_id}.")
            await self._ok("ai_spawn_ok", client_id, {"ai_id": ai_id, "name": name})
        else:
            await self._fail("ai_spawn_fail", client_id, "Failed to save AI entity.")

    async def handle_ai_remove_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "ai_remove_request",
            "ai_id": <str>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")

        if not ai_id:
            await self._fail("ai_remove_fail", client_id, "Missing ai_id.")
            return

        if not await self.ai_repository.ai_exists(ai_id):
            await self._fail("ai_remove_fail", client_id, f"AI '{ai_id}' does not exist.")
            return

        success = await self.ai_repository.remove_ai(ai_id)
        if success:
            self.logger.info(f"AI '{ai_id}' removed.")
            await self._ok("ai_remove_ok", client_id, {"ai_id": ai_id})
        else:
            await self._fail("ai_remove_fail", client_id, "Failed to remove AI.")

    async def handle_ai_move_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "ai_move_request",
            "ai_id": <str>,
            "dx": <float>,
            "dy": <float>,
            "dz": <float>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")
        dx = msg.get("dx", 0.0)
        dy = msg.get("dy", 0.0)
        dz = msg.get("dz", 0.0)

        if not ai_id:
            await self._fail("ai_move_fail", client_id, "Missing ai_id.")
            return

        ai_entity = await self.ai_repository.load_ai(ai_id)
        if not ai_entity:
            await self._fail("ai_move_fail", client_id, f"AI '{ai_id}' not found.")
            return

        ai_entity.move(dx, dy, dz)

        if await self.ai_repository.save_ai(ai_entity):
            await self._ok("ai_move_ok", client_id, {"ai_id": ai_id, "position": ai_entity.position})
        else:
            await self._fail("ai_move_fail", client_id, "Failed to save AI position.")

    async def handle_ai_update_health_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "ai_update_health_request",
            "ai_id": <str>,
            "amount": <int>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")
        amount = msg.get("amount")

        if not ai_id or amount is None:
            await self._fail("ai_update_health_fail", client_id, "Missing ai_id or amount.")
            return

        ai_entity = await self.ai_repository.load_ai(ai_id)
        if not ai_entity:
            await self._fail("ai_update_health_fail", client_id, f"AI '{ai_id}' not found.")
            return

        ai_entity.update_health(amount)
        if await self.ai_repository.save_ai(ai_entity):
            await self._ok("ai_update_health_ok", client_id, {"ai_id": ai_id, "health": ai_entity.health})
        else:
            await self._fail("ai_update_health_fail", client_id, "Failed to save AI health.")

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

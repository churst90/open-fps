# services/ai_service.py
import logging
from typing import Optional
from domain.ai.ai_entity import AIEntity
import uuid
from infrastructure.logging.custom_logger import get_logger

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
        self.logger = logger or get_logger("AIService", debug_mode=False)
        self.logger.debug("AIService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("ai_spawn_request", self.handle_ai_spawn_request)
        await self.event_dispatcher.subscribe("ai_remove_request", self.handle_ai_remove_request)
        await self.event_dispatcher.subscribe("ai_move_request", self.handle_ai_move_request)
        await self.event_dispatcher.subscribe("ai_update_health_request", self.handle_ai_update_health_request)
        self.logger.info("AIService subscribed to AI-related events.")

    async def handle_ai_spawn_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        name = msg.get("name")
        position = msg.get("position")
        health = msg.get("health", 100)
        speed = msg.get("speed", 1.0)
        role = msg.get("role", "npc")

        self.logger.debug(f"Handling ai_spawn_request from client_id='{client_id}', name='{name}', position={position}, health={health}, speed={speed}, role='{role}'.")

        if not name or not position:
            reason = "Missing name or position."
            self.logger.warning(f"AI spawn failed: {reason}")
            await self._fail("ai_spawn_fail", client_id, reason)
            return

        ai_id = str(uuid.uuid4())
        ai_entity = AIEntity(ai_id, name, tuple(position), health, speed, role)

        success = await self.ai_repository.save_ai(ai_entity)
        if success:
            self.logger.info(f"AI '{name}' spawned with ID {ai_id}.")
            await self._ok("ai_spawn_ok", client_id, {"ai_id": ai_id, "name": name})
        else:
            reason = "Failed to save AI entity."
            self.logger.error(reason)
            await self._fail("ai_spawn_fail", client_id, reason)

    async def handle_ai_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")

        self.logger.debug(f"Handling ai_remove_request from client_id='{client_id}', ai_id='{ai_id}'.")

        if not ai_id:
            reason = "Missing ai_id."
            self.logger.warning(f"AI remove failed: {reason}")
            await self._fail("ai_remove_fail", client_id, reason)
            return

        if not await self.ai_repository.ai_exists(ai_id):
            reason = f"AI '{ai_id}' does not exist."
            self.logger.warning(f"AI remove failed: {reason}")
            await self._fail("ai_remove_fail", client_id, reason)
            return

        success = await self.ai_repository.remove_ai(ai_id)
        if success:
            self.logger.info(f"AI '{ai_id}' removed successfully.")
            await self._ok("ai_remove_ok", client_id, {"ai_id": ai_id})
        else:
            reason = "Failed to remove AI."
            self.logger.error(reason)
            await self._fail("ai_remove_fail", client_id, reason)

    async def handle_ai_move_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")
        dx = msg.get("dx", 0.0)
        dy = msg.get("dy", 0.0)
        dz = msg.get("dz", 0.0)

        self.logger.debug(f"Handling ai_move_request from client_id='{client_id}', ai_id='{ai_id}', dx={dx}, dy={dy}, dz={dz}.")

        if not ai_id:
            reason = "Missing ai_id."
            self.logger.warning(f"AI move failed: {reason}")
            await self._fail("ai_move_fail", client_id, reason)
            return

        ai_entity = await self.ai_repository.load_ai(ai_id)
        if not ai_entity:
            reason = f"AI '{ai_id}' not found."
            self.logger.warning(f"AI move failed: {reason}")
            await self._fail("ai_move_fail", client_id, reason)
            return

        ai_entity.move(dx, dy, dz)
        self.logger.debug(f"AI '{ai_id}' moved to position {ai_entity.position}.")

        if await self.ai_repository.save_ai(ai_entity):
            await self._ok("ai_move_ok", client_id, {"ai_id": ai_id, "position": ai_entity.position})
        else:
            reason = "Failed to save AI position."
            self.logger.error(reason)
            await self._fail("ai_move_fail", client_id, reason)

    async def handle_ai_update_health_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        ai_id = msg.get("ai_id")
        amount = msg.get("amount")

        self.logger.debug(f"Handling ai_update_health_request from client_id='{client_id}', ai_id='{ai_id}', amount={amount}.")

        if not ai_id or amount is None:
            reason = "Missing ai_id or amount."
            self.logger.warning(f"AI health update failed: {reason}")
            await self._fail("ai_update_health_fail", client_id, reason)
            return

        ai_entity = await self.ai_repository.load_ai(ai_id)
        if not ai_entity:
            reason = f"AI '{ai_id}' not found."
            self.logger.warning(f"AI health update failed: {reason}")
            await self._fail("ai_update_health_fail", client_id, reason)
            return

        ai_entity.update_health(amount)
        self.logger.debug(f"AI '{ai_id}' health updated by {amount}. New health: {ai_entity.health}")

        if await self.ai_repository.save_ai(ai_entity):
            await self._ok("ai_update_health_ok", client_id, {"ai_id": ai_id, "health": ai_entity.health})
        else:
            reason = "Failed to save AI health."
            self.logger.error(reason)
            await self._fail("ai_update_health_fail", client_id, reason)

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

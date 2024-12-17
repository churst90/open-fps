# services/physics_service.py
import logging
from typing import Optional
from domain.physics.physics_engine import PhysicsEngine
from infrastructure.logging.custom_logger import get_logger

class PhysicsService:
    """
    The PhysicsService listens for events related to physics updates and applies
    changes to entities via the PhysicsEngine.

    Example events:
    - "physics_apply_gravity_request"
    - "physics_jump_request"
    - "physics_apply_force_request"

    Each event should specify which entity to apply physics to, their current state,
    and parameters for the action. The PhysicsService will then use PhysicsEngine to 
    compute the new state, and dispatch a success event with the updated state.
    """

    def __init__(self, event_dispatcher, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.physics_engine = PhysicsEngine()
        self.logger = logger or get_logger("PhysicsService", debug_mode=False)
        self.logger.debug("PhysicsService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("physics_apply_gravity_request", self.handle_apply_gravity_request)
        await self.event_dispatcher.subscribe("physics_jump_request", self.handle_jump_request)
        await self.event_dispatcher.subscribe("physics_apply_force_request", self.handle_apply_force_request)
        self.logger.info("PhysicsService subscribed to physics-related events.")

    async def handle_apply_gravity_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        self.logger.debug(f"Handling apply gravity request from client_id='{client_id}' with data={msg}")

        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        delta_time = msg.get("delta_time", 0.016)

        new_pos, new_vel = self.physics_engine.apply_gravity(position, velocity, delta_time)
        self.logger.debug(f"Applied gravity: old_pos={position}, old_vel={velocity}, new_pos={new_pos}, new_vel={new_vel}")
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)
        self.logger.debug(f"Position after collision adjustment: {new_pos}")

        await self._ok("physics_apply_gravity_ok", client_id, {"position": new_pos, "velocity": new_vel})
        self.logger.info("Gravity applied successfully and result dispatched.")

    async def handle_jump_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        self.logger.debug(f"Handling jump request from client_id='{client_id}' with data={msg}")

        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        jump_speed = msg.get("jump_speed", 5.0)

        new_pos, new_vel = self.physics_engine.jump(position, velocity, jump_speed)
        self.logger.debug(f"Applied jump: old_pos={position}, old_vel={velocity}, new_pos={new_pos}, new_vel={new_vel}")
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)
        self.logger.debug(f"Position after collision adjustment: {new_pos}")

        await self._ok("physics_jump_ok", client_id, {"position": new_pos, "velocity": new_vel})
        self.logger.info("Jump applied successfully and result dispatched.")

    async def handle_apply_force_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        self.logger.debug(f"Handling apply force request from client_id='{client_id}' with data={msg}")

        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        force = tuple(msg.get("force", [0,0,0]))
        mass = msg.get("mass", 1.0)
        delta_time = msg.get("delta_time", 0.016)

        new_pos, new_vel = self.physics_engine.apply_force(position, velocity, force, mass, delta_time)
        self.logger.debug(f"Applied force: old_pos={position}, old_vel={velocity}, force={force}, new_pos={new_pos}, new_vel={new_vel}")
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)
        self.logger.debug(f"Position after collision adjustment: {new_pos}")

        await self._ok("physics_apply_force_ok", client_id, {"position": new_pos, "velocity": new_vel})
        self.logger.info("Force applied successfully and result dispatched.")

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

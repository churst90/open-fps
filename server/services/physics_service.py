# services/physics_service.py
import logging
from typing import Optional
from domain.physics.physics_engine import PhysicsEngine

class PhysicsService:
    """
    The PhysicsService listens for events related to physics updates and applies
    changes to entities via the PhysicsEngine.

    Example events:
    - "physics_apply_gravity_request"
    - "physics_jump_request"
    - "physics_apply_force_request"

    Each event should specify which entity to apply physics to (e.g., a user or AI entity),
    their current state (position, velocity), and parameters for the action.
    The PhysicsService will use PhysicsEngine to compute the new state, then dispatch a 
    success event with the updated state.
    """

    def __init__(self, event_dispatcher, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.physics_engine = PhysicsEngine()
        self.logger = logger or logging.getLogger("PhysicsService")

    async def start(self):
        await self.event_dispatcher.subscribe("physics_apply_gravity_request", self.handle_apply_gravity_request)
        await self.event_dispatcher.subscribe("physics_jump_request", self.handle_jump_request)
        await self.event_dispatcher.subscribe("physics_apply_force_request", self.handle_apply_force_request)

    async def handle_apply_gravity_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "physics_apply_gravity_request",
            "entity_type": "user" or "ai",
            "entity_id": <str>,
            "position": [x, y, z],
            "velocity": [vx, vy, vz],
            "delta_time": <float, optional>
          }
        }

        The service should:
        - Load the entity if needed (or just trust the provided position/velocity).
        - Apply gravity.
        - Possibly save updated state back to a repository or dispatch a result event.
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        delta_time = msg.get("delta_time", 0.016)

        new_pos, new_vel = self.physics_engine.apply_gravity(position, velocity, delta_time)
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)

        # Dispatch result
        await self._ok("physics_apply_gravity_ok", client_id, {"position": new_pos, "velocity": new_vel})

    async def handle_jump_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "physics_jump_request",
            "entity_id": <str>,
            "position": [x, y, z],
            "velocity": [vx, vy, vz],
            "jump_speed": <float>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        jump_speed = msg.get("jump_speed", 5.0)

        new_pos, new_vel = self.physics_engine.jump(position, velocity, jump_speed)
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)

        await self._ok("physics_jump_ok", client_id, {"position": new_pos, "velocity": new_vel})

    async def handle_apply_force_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "physics_apply_force_request",
            "entity_id": <str>,
            "position": [x, y, z],
            "velocity": [vx, vy, vz],
            "force": [Fx, Fy, Fz],
            "mass": <float, optional>,
            "delta_time": <float, optional>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        position = tuple(msg.get("position", [0,0,0]))
        velocity = tuple(msg.get("velocity", [0,0,0]))
        force = tuple(msg.get("force", [0,0,0]))
        mass = msg.get("mass", 1.0)
        delta_time = msg.get("delta_time", 0.016)

        new_pos, new_vel = self.physics_engine.apply_force(position, velocity, force, mass, delta_time)
        new_pos = self.physics_engine.check_collisions_and_adjust(new_pos)

        await self._ok("physics_apply_force_ok", client_id, {"position": new_pos, "velocity": new_vel})

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

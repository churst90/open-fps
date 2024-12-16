# services/weapon_service.py
import logging
from typing import Optional
import uuid

from domain.weapons.weapon import Weapon

class WeaponService:
    """
    Handles events like:
    - "weapon_create_request" (create a new weapon)
    - "weapon_fire_request" (fire weapon if ammo available)
    - "weapon_reload_request" (reload weapon)
    - "weapon_equip_request" (equip a weapon to a user or AI)
    - "weapon_unequip_request" (unequip a weapon)
    """

    def __init__(self, event_dispatcher, weapon_repository, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.weapon_repository = weapon_repository
        self.logger = logger or logging.getLogger("WeaponService")

    async def start(self):
        await self.event_dispatcher.subscribe("weapon_create_request", self.handle_weapon_create_request)
        await self.event_dispatcher.subscribe("weapon_fire_request", self.handle_weapon_fire_request)
        await self.event_dispatcher.subscribe("weapon_reload_request", self.handle_weapon_reload_request)
        # Add equip/unequip events as needed

    async def handle_weapon_create_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "weapon_create_request",
            "name": <str>,
            "damage": <float>,
            "range": <float>,
            "fire_rate": <float>,
            "reload_time": <float>,
            "ammo_capacity": <int>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]

        name = msg.get("name")
        damage = msg.get("damage", 10.0)
        range_ = msg.get("range", 50.0)
        fire_rate = msg.get("fire_rate", 1.0)
        reload_time = msg.get("reload_time", 2.0)
        ammo_capacity = msg.get("ammo_capacity", 30)

        if not name:
            await self._fail("weapon_create_fail", client_id, "Missing weapon name.")
            return

        weapon_id = str(uuid.uuid4())
        weapon = Weapon(weapon_id, name, damage, range_, fire_rate, reload_time, ammo_capacity, current_ammo=ammo_capacity)

        success = await self.weapon_repository.save_weapon(weapon)
        if success:
            self.logger.info(f"Weapon '{name}' created with ID {weapon_id}.")
            await self._ok("weapon_create_ok", client_id, {"weapon_id": weapon_id, "name": name})
        else:
            await self._fail("weapon_create_fail", client_id, "Failed to save weapon.")

    async def handle_weapon_fire_request(self, event_data):
        # Implement firing logic: check if weapon exists, ammo > 0, decrement ammo, handle cooldown, etc.
        pass

    async def handle_weapon_reload_request(self, event_data):
        # Implement reload logic: set current_ammo to ammo_capacity after reload_time
        pass

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

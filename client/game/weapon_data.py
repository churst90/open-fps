# game/weapon_data.py
import logging
from typing import Dict, Any

class WeaponData:
    """
    Stores information about known weapons:
    - weapon_id
    - name
    - damage, range, accuracy, fire_rate, reload_time, ammo_capacity, etc.

    The server may provide a list of weapons or updates when certain weapons are introduced.
    """

    def __init__(self):
        self.logger = logging.getLogger("WeaponData")
        self.weapons: Dict[str, Dict[str, Any]] = {}
        # weapons[weapon_id] = {
        #   "name": str,
        #   "damage": int/float,
        #   "range": float,
        #   "accuracy": float,
        #   "fire_rate": float,
        #   "reload_time": float,
        #   "ammo_capacity": int,
        #   ...other properties...
        # }

    def load_weapons(self, data: Dict[str, Dict[str, Any]]):
        """
        Load multiple weapons at once.
        data format: {weapon_id: {"name":..., "damage":..., "range":..., ...}, ...}
        """
        self.weapons = data
        self.logger.debug(f"Loaded {len(data)} weapons.")

    def add_weapon(self, weapon_id: str, properties: Dict[str, Any]):
        """
        Add or update a single weapon definition.
        """
        self.weapons[weapon_id] = properties
        self.logger.debug(f"Weapon {weapon_id} added/updated.")

    def remove_weapon(self, weapon_id: str):
        """
        Remove a weapon definition by weapon_id.
        """
        if weapon_id in self.weapons:
            del self.weapons[weapon_id]
            self.logger.debug(f"Weapon {weapon_id} removed.")

    def get_weapon_info(self, weapon_id: str) -> Dict[str, Any]:
        """
        Get info for a particular weapon.
        Returns empty dict if not found.
        """
        return self.weapons.get(weapon_id, {})

    def list_weapons(self) -> Dict[str, Dict[str, Any]]:
        """
        Return all known weapons.
        """
        return self.weapons

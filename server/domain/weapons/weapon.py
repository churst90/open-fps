# domain/weapons/weapon.py
from typing import Dict, Any

class Weapon:
    """
    Represents a weapon with various properties:
    - weapon_id: Unique ID
    - name: Weapon name
    - damage: Damage per hit
    - range: Maximum range of the weapon
    - fire_rate: Shots per second
    - reload_time: Time to reload
    - ammo_capacity: Max ammo capacity
    - current_ammo: Current ammo loaded
    - properties: Additional properties (spread, recoil, etc.)
    """

    def __init__(self, weapon_id: str, name: str, damage: float, range_: float, fire_rate: float, reload_time: float, ammo_capacity: int, current_ammo: int, properties: Dict[str, Any] = None):
        self.weapon_id = weapon_id
        self.name = name
        self.damage = damage
        self.range = range_
        self.fire_rate = fire_rate
        self.reload_time = reload_time
        self.ammo_capacity = ammo_capacity
        self.current_ammo = current_ammo
        self.properties = properties or {}

    def to_dict(self):
        return {
            "weapon_id": self.weapon_id,
            "name": self.name,
            "damage": self.damage,
            "range": self.range,
            "fire_rate": self.fire_rate,
            "reload_time": self.reload_time,
            "ammo_capacity": self.ammo_capacity,
            "current_ammo": self.current_ammo,
            "properties": self.properties
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            weapon_id=data["weapon_id"],
            name=data["name"],
            damage=data["damage"],
            range_=data["range"],
            fire_rate=data["fire_rate"],
            reload_time=data["reload_time"],
            ammo_capacity=data["ammo_capacity"],
            current_ammo=data["current_ammo"],
            properties=data.get("properties", {})
        )

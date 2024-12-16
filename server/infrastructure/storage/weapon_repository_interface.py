# infrastructure/storage/weapon_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.weapons.weapon import Weapon

class WeaponRepositoryInterface(ABC):
    @abstractmethod
    async def save_weapon(self, weapon: Weapon) -> bool:
        pass

    @abstractmethod
    async def load_weapon(self, weapon_id: str) -> Optional[Weapon]:
        pass

    @abstractmethod
    async def remove_weapon(self, weapon_id: str) -> bool:
        pass

    @abstractmethod
    async def weapon_exists(self, weapon_id: str) -> bool:
        pass

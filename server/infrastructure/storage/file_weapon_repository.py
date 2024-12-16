# infrastructure/storage/file_weapon_repository.py
import aiofiles
import json
import os
import logging
from typing import Optional
from .weapon_repository_interface import WeaponRepositoryInterface
from domain.weapons.weapon import Weapon

class FileWeaponRepository(WeaponRepositoryInterface):
    def __init__(self, weapons_dir: str = "weapons", logger: Optional[logging.Logger] = None):
        self.weapons_dir = weapons_dir
        self.logger = logger or logging.getLogger("FileWeaponRepository")
        os.makedirs(self.weapons_dir, exist_ok=True)

    def _weapon_file_path(self, weapon_id: str) -> str:
        return os.path.join(self.weapons_dir, f"{weapon_id}.json")

    async def save_weapon(self, weapon: Weapon) -> bool:
        path = self._weapon_file_path(weapon.weapon_id)
        data = weapon.to_dict()
        try:
            async with aiofiles.open(path, "w") as f:
                await f.write(json.dumps(data, ensure_ascii=False))
            return True
        except Exception as e:
            self.logger.exception(f"Error saving weapon '{weapon.weapon_id}': {e}")
            return False

    async def load_weapon(self, weapon_id: str) -> Optional[Weapon]:
        path = self._weapon_file_path(weapon_id)
        if not os.path.exists(path):
            return None
        try:
            async with aiofiles.open(path, "r") as f:
                data = json.loads(await f.read())
            return Weapon.from_dict(data)
        except Exception as e:
            self.logger.exception(f"Error loading weapon '{weapon_id}': {e}")
            return None

    async def remove_weapon(self, weapon_id: str) -> bool:
        path = self._weapon_file_path(weapon_id)
        if not os.path.exists(path):
            return False
        try:
            os.remove(path)
            return True
        except Exception as e:
            self.logger.exception(f"Error removing weapon '{weapon_id}': {e}")
            return False

    async def weapon_exists(self, weapon_id: str) -> bool:
        return os.path.exists(self._weapon_file_path(weapon_id))

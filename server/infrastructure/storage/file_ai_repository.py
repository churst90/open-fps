# infrastructure/storage/file_ai_repository.py
import aiofiles
import json
import os
import logging
from typing import Optional
from domain.ai.ai_entity import AIEntity

class FileAIRepository:
    def __init__(self, ai_dir: str = "ai_data", logger: Optional[logging.Logger] = None):
        self.ai_dir = ai_dir
        self.logger = logger or logging.getLogger("FileAIRepository")
        os.makedirs(self.ai_dir, exist_ok=True)

    def _ai_file_path(self, ai_id: str) -> str:
        return os.path.join(self.ai_dir, f"{ai_id}.json")

    async def save_ai(self, ai_entity: AIEntity) -> bool:
        ai_path = self._ai_file_path(ai_entity.ai_id)
        data = {
            "ai_id": ai_entity.ai_id,
            "name": ai_entity.name,
            "position": ai_entity.position,
            "health": ai_entity.health,
            "speed": ai_entity.speed,
            "role": ai_entity.role,
            "map_name": getattr(ai_entity, "map_name", None)  # Ensure map_name is stored
        }
        try:
            async with aiofiles.open(ai_path, "w") as f:
                await f.write(json.dumps(data, ensure_ascii=False))
            self.logger.info(f"Saved AI '{ai_entity.ai_id}'.")
            return True
        except Exception as e:
            self.logger.exception(f"Error saving AI '{ai_entity.ai_id}': {e}")
            return False

    async def load_ai(self, ai_id: str) -> Optional[AIEntity]:
        ai_path = self._ai_file_path(ai_id)
        if not os.path.exists(ai_path):
            return None
        try:
            async with aiofiles.open(ai_path, "r") as f:
                data = json.loads(await f.read())
            # AIEntity constructor: AIEntity(ai_id, name, position, health, speed, role)
            ai = AIEntity(
                ai_id=data["ai_id"],
                name=data["name"],
                position=tuple(data["position"]),
                health=data["health"],
                speed=data["speed"],
                role=data.get("role", "npc")
            )
            if "map_name" in data:
                setattr(ai, "map_name", data["map_name"])
            return ai
        except Exception as e:
            self.logger.exception(f"Error loading AI '{ai_id}': {e}")
            return None

    async def remove_ai(self, ai_id: str) -> bool:
        ai_path = self._ai_file_path(ai_id)
        if not os.path.exists(ai_path):
            return False
        try:
            os.remove(ai_path)
            self.logger.info(f"Removed AI '{ai_id}'.")
            return True
        except Exception as e:
            self.logger.exception(f"Error removing AI '{ai_id}': {e}")
            return False

    async def ai_exists(self, ai_id: str) -> bool:
        return os.path.exists(self._ai_file_path(ai_id))

    async def get_ai_by_map(self, map_name: str) -> list:
        ai_list = []
        for ai_file in os.listdir(self.ai_dir):
            if ai_file.endswith(".json"):
                ai_id = ai_file[:-5]
                ai = await self.load_ai(ai_id)
                if ai and getattr(ai, "map_name", None) == map_name:
                    ai_list.append(ai)
        return ai_list

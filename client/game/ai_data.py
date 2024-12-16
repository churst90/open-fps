# game/ai_data.py
import logging
from typing import Dict, Any

class AIData:
    """
    Manages information about AI entities (NPCs, monsters, guards, etc.) known to the client.
    The server sends data about AI positions, health, and possibly behaviors.
    """

    def __init__(self):
        self.logger = logging.getLogger("AIData")
        self.ai_entities: Dict[str, Dict[str, Any]] = {}
        # ai_entities[ai_id] = {
        #   "name": str,
        #   "position": (x,y,z),
        #   "health": int,
        #   "speed": float,
        #   "role": str (e.g. "npc", "monster"),
        #   ...other properties...
        # }

    def load_ai(self, data: Dict[str, Dict[str, Any]]):
        """
        Load multiple AI entities at once.
        data format: {ai_id: {...}, ...} with attributes like name, position, health, etc.
        """
        self.ai_entities = data
        self.logger.debug(f"Loaded {len(data)} AI entities.")

    def add_or_update_ai(self, ai_id: str, ai_data: Dict[str, Any]):
        """
        Add or update a single AI entity's data.
        """
        self.ai_entities[ai_id] = ai_data
        self.logger.debug(f"AI {ai_id} added/updated.")

    def remove_ai(self, ai_id: str):
        """
        Remove an AI entity from the local data.
        """
        if ai_id in self.ai_entities:
            del self.ai_entities[ai_id]
            self.logger.debug(f"AI {ai_id} removed.")

    def get_ai_info(self, ai_id: str) -> Dict[str, Any]:
        """
        Get info about a specific AI by ai_id.
        Returns empty dict if not found.
        """
        return self.ai_entities.get(ai_id, {})

    def list_ai(self) -> Dict[str, Dict[str, Any]]:
        """
        Return a dictionary of all known AI entities.
        """
        return self.ai_entities

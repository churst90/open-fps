# game/entity_data.py
import logging
from typing import Dict, Any

class EntityData:
    """
    A generic module to store and manage data for various entities in the game.
    While player and AI have their own modules, EntityData could handle other
    NPCs, static objects with positions, or special entities not covered elsewhere.

    For example, you might have environment entities (doors, levers), or special
    non-AI characters that don't fit into ai_data or player_data.
    """

    def __init__(self):
        self.logger = logging.getLogger("EntityData")
        self.entities: Dict[str, Dict[str, Any]] = {}
        # entities[entity_id] = {
        #   "type": str (e.g., "door", "lever", "chest"),
        #   "position": (x,y,z),
        #   "state": {...} (open/closed, locked/unlocked),
        #   ...other attributes...
        # }

    def load_entities(self, data: Dict[str, Dict[str, Any]]):
        """
        Load multiple entities from a dictionary.
        data format: {entity_id: {"type":..., "position":..., "state":...}, ...}
        """
        self.entities = data
        self.logger.debug(f"Loaded {len(data)} entities.")

    def add_or_update_entity(self, entity_id: str, entity_data: Dict[str, Any]):
        """
        Add or update a single entity definition.
        """
        self.entities[entity_id] = entity_data
        self.logger.debug(f"Entity {entity_id} added/updated.")

    def remove_entity(self, entity_id: str):
        """
        Remove an entity by entity_id.
        """
        if entity_id in self.entities:
            del self.entities[entity_id]
            self.logger.debug(f"Entity {entity_id} removed.")

    def get_entity_info(self, entity_id: str) -> Dict[str, Any]:
        """
        Get info for a particular entity.
        Returns empty dict if not found.
        """
        return self.entities.get(entity_id, {})

    def list_entities(self) -> Dict[str, Dict[str, Any]]:
        """
        Return all known entities.
        """
        return self.entities

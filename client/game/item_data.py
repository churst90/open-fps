# game/item_data.py
import logging
from typing import Dict, Any

class ItemData:
    """
    Represents item definitions and properties client-side.
    The server may send a list of items or item updates, which we store here.
    For now, we assume items are identified by an item_id and have a name and properties.
    """

    def __init__(self):
        self.logger = logging.getLogger("ItemData")
        self.items: Dict[str, Dict[str, Any]] = {}
        # items[item_id] = {
        #   "name": str,
        #   "properties": {...} (density, volume, durability, etc.)
        # }

    def load_items(self, data: Dict[str, Dict[str, Any]]):
        """
        Load multiple items from a dictionary.
        data format: {item_id: {"name":..., "properties":...}, ...}
        """
        self.items = data
        self.logger.debug(f"Loaded {len(data)} items.")

    def add_item(self, item_id: str, name: str, properties: Dict[str, Any]):
        """
        Add or update a single item definition.
        """
        self.items[item_id] = {"name": name, "properties": properties}
        self.logger.debug(f"Item {item_id} added/updated.")

    def remove_item(self, item_id: str):
        """
        Remove an item definition if it exists.
        """
        if item_id in self.items:
            del self.items[item_id]
            self.logger.debug(f"Item {item_id} removed.")

    def get_item_info(self, item_id: str) -> Dict[str, Any]:
        """
        Get the info of an item by item_id, return dict or empty if not found.
        """
        return self.items.get(item_id, {})

    def list_items(self) -> Dict[str, Dict[str, Any]]:
        """
        Return all items currently known.
        """
        return self.items

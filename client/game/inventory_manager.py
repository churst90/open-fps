# game/inventory_manager.py
import logging
from typing import Dict, Any

class InventoryManager:
    """
    Manages the player's inventory items locally.
    Each item in the inventory might be represented as {item_id: quantity}, or possibly more detail.
    The server sends inventory updates after certain events (pickup, drop, craft).
    """

    def __init__(self):
        self.logger = logging.getLogger("InventoryManager")
        self.inventory: Dict[str, Any] = {}
        # inventory[item_id] = { "quantity": int, possibly other attributes like "condition" }

    def load_inventory(self, data: Dict[str, Any]):
        """
        Load full inventory data from a dictionary provided by the server.
        data format might be:
        {
          "items": {item_id: {"quantity":int, ...}, ...}
        }
        """
        items = data.get("items", {})
        self.inventory = items
        self.logger.debug(f"Loaded inventory with {len(items)} items.")

    def add_item(self, item_id: str, quantity: int = 1):
        """
        Add or increase the quantity of an item in the inventory.
        """
        if item_id not in self.inventory:
            self.inventory[item_id] = {"quantity": quantity}
        else:
            self.inventory[item_id]["quantity"] += quantity
        self.logger.debug(f"Added {quantity} of {item_id}. Now have {self.inventory[item_id]['quantity']}.")

    def remove_item(self, item_id: str, quantity: int = 1):
        """
        Remove or reduce the quantity of an item.
        If quantity drops to 0 or below, remove the item entirely.
        """
        if item_id in self.inventory:
            self.inventory[item_id]["quantity"] -= quantity
            if self.inventory[item_id]["quantity"] <= 0:
                del self.inventory[item_id]
                self.logger.debug(f"Item {item_id} removed from inventory.")
            else:
                self.logger.debug(f"Removed {quantity} of {item_id}. Left with {self.inventory[item_id]['quantity']}.")

    def get_item_quantity(self, item_id: str) -> int:
        """
        Get the quantity of a given item_id in the inventory.
        """
        if item_id in self.inventory:
            return self.inventory[item_id]["quantity"]
        return 0

    def list_inventory(self) -> Dict[str, Any]:
        """
        Return the entire inventory dictionary.
        """
        return self.inventory

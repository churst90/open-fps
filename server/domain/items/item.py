# domain/items/item.py
from typing import Dict, Any

class Item:
    """
    Represents a generic item in the game world. Items can be materials, crafted objects,
    weapons, etc.

    Attributes:
    - item_id: Unique identifier for the item.
    - name: Name of the item.
    - properties: A dictionary for additional item properties like density, volume,
      durability, etc.
    """

    def __init__(self, item_id: str, name: str, properties: Dict[str, Any] = None):
        self.item_id = item_id
        self.name = name
        self.properties = properties or {}

    def to_dict(self) -> dict:
        return {
            "item_id": self.item_id,
            "name": self.name,
            "properties": self.properties
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            item_id=data["item_id"],
            name=data["name"],
            properties=data.get("properties", {})
        )

# domain/items/material.py
from typing import Dict, Any

class Material:
    """
    A specific type of item that serves as a material for crafting.
    Materials have physical properties like density, volume, hardness, etc.
    """

    def __init__(self, material_id: str, name: str, properties: Dict[str, Any] = None):
        """
        :param material_id: Unique identifier for the material.
        :param name: Name of the material (e.g., "Iron", "Wood").
        :param properties: Dictionary of physical properties (density, volume, etc.).
        """
        self.material_id = material_id
        self.name = name
        self.properties = properties or {}

    def to_dict(self) -> dict:
        return {
            "material_id": self.material_id,
            "name": self.name,
            "properties": self.properties
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            material_id=data["material_id"],
            name=data["name"],
            properties=data.get("properties", {})
        )

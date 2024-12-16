# domain/items/recipe.py
from typing import Dict, Any, List

class Recipe:
    """
    Defines a crafting recipe.
    
    Attributes:
    - recipe_id: Unique ID for this recipe.
    - result_item_id: The item_id of the resulting item.
    - ingredients: A dictionary mapping material_id/item_id to quantities needed.
    - properties: Additional recipe properties (time to craft, skill required, etc.).
    """

    def __init__(self, recipe_id: str, result_item_id: str, ingredients: Dict[str, int], properties: Dict[str, Any] = None):
        self.recipe_id = recipe_id
        self.result_item_id = result_item_id
        self.ingredients = ingredients
        self.properties = properties or {}

    def to_dict(self) -> dict:
        return {
            "recipe_id": self.recipe_id,
            "result_item_id": self.result_item_id,
            "ingredients": self.ingredients,
            "properties": self.properties
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            recipe_id=data["recipe_id"],
            result_item_id=data["result_item_id"],
            ingredients=data["ingredients"],
            properties=data.get("properties", {})
        )

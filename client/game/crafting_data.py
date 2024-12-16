# game/crafting_data.py
import logging
from typing import Dict, Any

class CraftingData:
    """
    Manages crafting recipes and logic.
    The server might send a list of recipes or updates when new recipes are added.
    Each recipe specifies a result item and required ingredients.
    """

    def __init__(self):
        self.logger = logging.getLogger("CraftingData")
        self.recipes: Dict[str, Dict[str, Any]] = {}
        # recipes[recipe_id] = {
        #   "result_item_id": str,
        #   "ingredients": {material_id/item_id: quantity, ...},
        #   "properties": {...} (e.g., crafting time, skill required)
        # }

    def load_recipes(self, data: Dict[str, Dict[str, Any]]):
        """
        Load multiple recipes from a dictionary.
        data format: {recipe_id: {"result_item_id":..., "ingredients":..., "properties":...}, ...}
        """
        self.recipes = data
        self.logger.debug(f"Loaded {len(data)} crafting recipes.")

    def add_or_update_recipe(self, recipe_id: str, recipe_data: Dict[str, Any]):
        """
        Add or update a single recipe.
        """
        self.recipes[recipe_id] = recipe_data
        self.logger.debug(f"Recipe {recipe_id} added/updated.")

    def remove_recipe(self, recipe_id: str):
        """
        Remove a recipe by recipe_id.
        """
        if recipe_id in self.recipes:
            del self.recipes[recipe_id]
            self.logger.debug(f"Recipe {recipe_id} removed.")

    def get_recipe_info(self, recipe_id: str) -> Dict[str, Any]:
        """
        Get details about a specific recipe. Returns empty dict if not found.
        """
        return self.recipes.get(recipe_id, {})

    def list_recipes(self) -> Dict[str, Dict[str, Any]]:
        """
        Return all known recipes.
        """
        return self.recipes

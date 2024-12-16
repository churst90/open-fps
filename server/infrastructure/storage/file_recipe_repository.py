# infrastructure/storage/file_recipe_repository.py
import aiofiles
import json
import os
import logging
from typing import Optional
from .recipe_repository_interface import RecipeRepositoryInterface
from domain.items.recipe import Recipe

class FileRecipeRepository(RecipeRepositoryInterface):
    """
    Stores recipes as JSON files:
      recipes/
        <recipe_id>.json
    """

    def __init__(self, recipes_dir: str = "recipes", logger: Optional[logging.Logger] = None):
        self.recipes_dir = recipes_dir
        self.logger = logger or logging.getLogger("FileRecipeRepository")
        os.makedirs(self.recipes_dir, exist_ok=True)

    def _recipe_file_path(self, recipe_id: str) -> str:
        return os.path.join(self.recipes_dir, f"{recipe_id}.json")

    async def save_recipe(self, recipe: Recipe) -> bool:
        path = self._recipe_file_path(recipe.recipe_id)
        data = recipe.to_dict()
        try:
            async with aiofiles.open(path, "w") as f:
                await f.write(json.dumps(data, ensure_ascii=False))
            return True
        except Exception as e:
            self.logger.exception(f"Error saving recipe '{recipe.recipe_id}': {e}")
            return False

    async def load_recipe(self, recipe_id: str) -> Optional[Recipe]:
        path = self._recipe_file_path(recipe_id)
        if not os.path.exists(path):
            return None
        try:
            async with aiofiles.open(path, "r") as f:
                data = json.loads(await f.read())
            return Recipe.from_dict(data)
        except Exception as e:
            self.logger.exception(f"Error loading recipe '{recipe_id}': {e}")
            return None

    async def remove_recipe(self, recipe_id: str) -> bool:
        path = self._recipe_file_path(recipe_id)
        if not os.path.exists(path):
            return False
        try:
            os.remove(path)
            return True
        except Exception as e:
            self.logger.exception(f"Error removing recipe '{recipe_id}': {e}")
            return False

    async def recipe_exists(self, recipe_id: str) -> bool:
        path = self._recipe_file_path(recipe_id)
        return os.path.exists(path)

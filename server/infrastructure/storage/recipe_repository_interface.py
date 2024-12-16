# infrastructure/storage/recipe_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.items.recipe import Recipe

class RecipeRepositoryInterface(ABC):
    @abstractmethod
    async def save_recipe(self, recipe: Recipe) -> bool:
        pass

    @abstractmethod
    async def load_recipe(self, recipe_id: str) -> Optional[Recipe]:
        pass

    @abstractmethod
    async def remove_recipe(self, recipe_id: str) -> bool:
        pass

    @abstractmethod
    async def recipe_exists(self, recipe_id: str) -> bool:
        pass

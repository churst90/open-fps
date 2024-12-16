# services/crafting_service.py
import logging
from typing import Optional
import uuid

from domain.items.item import Item

class CraftingService:
    """
    The CraftingService handles crafting requests:
    - "craft_item_request" event: given materials (by IDs and quantities), check against a recipe,
      and if all ingredients are available, produce the result item.

    Dependencies:
    - recipe_repository: to load and verify recipes
    - item_repository: to create the crafted item
    """

    def __init__(self, event_dispatcher, recipe_repository, item_repository, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.recipe_repository = recipe_repository
        self.item_repository = item_repository
        self.logger = logger or logging.getLogger("CraftingService")

    async def start(self):
        await self.event_dispatcher.subscribe("craft_item_request", self.handle_craft_item_request)

    async def handle_craft_item_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "craft_item_request",
            "recipe_id": <str>,
            "materials": {material_id or item_id: quantity, ...}
          }
        }

        Steps:
        - Load the recipe
        - Check if materials match the recipe ingredients and if quantities are sufficient
        - If valid, create the resulting item via item_repository
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        recipe_id = msg.get("recipe_id")
        materials = msg.get("materials", {})

        if not recipe_id:
            await self._fail("craft_item_fail", client_id, "Missing recipe_id.")
            return

        recipe = await self.recipe_repository.load_recipe(recipe_id)
        if not recipe:
            await self._fail("craft_item_fail", client_id, f"Recipe '{recipe_id}' not found.")
            return

        # Check ingredients
        for ingredient_id, required_qty in recipe.ingredients.items():
            if materials.get(ingredient_id, 0) < required_qty:
                await self._fail("craft_item_fail", client_id, f"Insufficient '{ingredient_id}'. Need {required_qty}.")
                return

        # All ingredients are satisfied, create the result item
        result_item_id = str(uuid.uuid4())
        # For simplicity, we just give the item the name = recipe.result_item_id, or you can load item template
        result_item = Item(result_item_id, recipe.result_item_id, {})

        success = await self.item_repository.save_item(result_item)
        if success:
            self.logger.info(f"Crafted item '{recipe.result_item_id}' with ID {result_item_id} using recipe '{recipe_id}'.")
            await self._ok("craft_item_ok", client_id, {"recipe_id": recipe_id, "item_id": result_item_id})
        else:
            await self._fail("craft_item_fail", client_id, "Failed to create crafted item.")

    async def _ok(self, event_type: str, client_id: str, data: dict):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

# services/item_service.py
import logging
from typing import Optional
import uuid

from domain.items.item import Item

class ItemService:
    """
    The ItemService manages the lifecycle of items:
    - Creating new items
    - Deleting items
    - Listing items owned by a user or present in a location
    - Handling events like "item_create_request", "item_pickup_request", "item_drop_request"
    """

    def __init__(self, event_dispatcher, item_repository, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.item_repository = item_repository
        self.logger = logger or logging.getLogger("ItemService")

    async def start(self):
        await self.event_dispatcher.subscribe("item_create_request", self.handle_item_create_request)
        await self.event_dispatcher.subscribe("item_delete_request", self.handle_item_delete_request)
        # Add more subscriptions as needed (pickup, drop, etc.)

    async def handle_item_create_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "item_create_request",
            "name": <str>,
            "properties": {...}
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]

        name = msg.get("name")
        properties = msg.get("properties", {})

        if not name:
            await self._fail("item_create_fail", client_id, "Missing item name.")
            return

        item_id = str(uuid.uuid4())
        item = Item(item_id, name, properties)

        success = await self.item_repository.save_item(item)
        if success:
            self.logger.info(f"Item '{name}' created with ID {item_id}.")
            await self._ok("item_create_ok", client_id, {"item_id": item_id, "name": name})
        else:
            await self._fail("item_create_fail", client_id, "Failed to save item.")

    async def handle_item_delete_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "item_delete_request",
            "item_id": <str>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        item_id = msg.get("item_id")

        if not item_id:
            await self._fail("item_delete_fail", client_id, "Missing item_id.")
            return

        if not await self.item_repository.item_exists(item_id):
            await self._fail("item_delete_fail", client_id, f"Item '{item_id}' does not exist.")
            return

        success = await self.item_repository.remove_item(item_id)
        if success:
            self.logger.info(f"Item '{item_id}' deleted.")
            await self._ok("item_delete_ok", client_id, {"item_id": item_id})
        else:
            await self._fail("item_delete_fail", client_id, "Failed to remove item.")

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

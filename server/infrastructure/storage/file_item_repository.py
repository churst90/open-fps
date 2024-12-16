# infrastructure/storage/file_item_repository.py
import aiofiles
import json
import os
import logging
from typing import Optional
from .item_repository_interface import ItemRepositoryInterface
from domain.items.item import Item

class FileItemRepository(ItemRepositoryInterface):
    """
    Stores items as JSON files in a directory.
      items/
        <item_id>.json
    """

    def __init__(self, items_dir: str = "items", logger: Optional[logging.Logger] = None):
        self.items_dir = items_dir
        self.logger = logger or logging.getLogger("FileItemRepository")
        os.makedirs(self.items_dir, exist_ok=True)

    def _item_file_path(self, item_id: str) -> str:
        return os.path.join(self.items_dir, f"{item_id}.json")

    async def save_item(self, item: Item) -> bool:
        path = self._item_file_path(item.item_id)
        data = item.to_dict()
        try:
            async with aiofiles.open(path, "w") as f:
                await f.write(json.dumps(data, ensure_ascii=False))
            return True
        except Exception as e:
            self.logger.exception(f"Error saving item '{item.item_id}': {e}")
            return False

    async def load_item(self, item_id: str) -> Optional[Item]:
        path = self._item_file_path(item_id)
        if not os.path.exists(path):
            return None
        try:
            async with aiofiles.open(path, "r") as f:
                data = json.loads(await f.read())
            return Item.from_dict(data)
        except Exception as e:
            self.logger.exception(f"Error loading item '{item_id}': {e}")
            return None

    async def remove_item(self, item_id: str) -> bool:
        path = self._item_file_path(item_id)
        if not os.path.exists(path):
            return False
        try:
            os.remove(path)
            return True
        except Exception as e:
            self.logger.exception(f"Error removing item '{item_id}': {e}")
            return False

    async def item_exists(self, item_id: str) -> bool:
        path = self._item_file_path(item_id)
        return os.path.exists(path)

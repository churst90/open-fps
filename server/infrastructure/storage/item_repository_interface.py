# infrastructure/storage/item_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.items.item import Item

class ItemRepositoryInterface(ABC):
    @abstractmethod
    async def save_item(self, item: Item) -> bool:
        pass

    @abstractmethod
    async def load_item(self, item_id: str) -> Optional[Item]:
        pass

    @abstractmethod
    async def remove_item(self, item_id: str) -> bool:
        pass

    @abstractmethod
    async def item_exists(self, item_id: str) -> bool:
        pass

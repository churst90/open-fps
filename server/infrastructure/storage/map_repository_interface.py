# infrastructure/storage/map_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.maps.map import Map

class MapRepositoryInterface(ABC):

    @abstractmethod
    async def save_map(self, game_map: Map) -> bool:
        """
        Save or update the given map.
        :return: True if the operation succeeded, False otherwise.
        """
        pass

    @abstractmethod
    async def load_map(self, map_name: str) -> Optional[Map]:
        """
        Load a map by its name.
        :return: A Map instance if found, None otherwise.
        """
        pass

    @abstractmethod
    async def remove_map(self, map_name: str) -> bool:
        """
        Remove a map by name.
        :return: True if removed successfully, False otherwise.
        """
        pass

    @abstractmethod
    async def map_exists(self, map_name: str) -> bool:
        """
        Check if a map with the given name exists.
        :return: True if the map exists, False otherwise.
        """
        pass

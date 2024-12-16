# infrastructure/storage/ai_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.ai.ai_entity import AIEntity

class AIRepositoryInterface(ABC):
    """
    Defines the interface for AI repository implementations.
    Methods for saving, loading, removing, and checking existence of AI entities.
    """

    @abstractmethod
    async def save_ai(self, ai_entity: AIEntity) -> bool:
        """
        Save or update the given AI entity.
        
        :param ai_entity: The AIEntity instance to save.
        :return: True if saved successfully, False otherwise.
        """
        pass

    @abstractmethod
    async def load_ai(self, ai_id: str) -> Optional[AIEntity]:
        """
        Load an AI entity by its ID.
        
        :param ai_id: The unique ID of the AI.
        :return: AIEntity if found, None otherwise.
        """
        pass

    @abstractmethod
    async def remove_ai(self, ai_id: str) -> bool:
        """
        Remove an AI entity by its ID.
        
        :param ai_id: The unique ID of the AI.
        :return: True if removed, False if not found or error occurred.
        """
        pass

    @abstractmethod
    async def ai_exists(self, ai_id: str) -> bool:
        """
        Check if an AI with the given ID exists.
        
        :param ai_id: The AI ID.
        :return: True if exists, False otherwise.
        """
        pass

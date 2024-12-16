# infrastructure/storage/user_repository_interface.py
from typing import Optional
from abc import ABC, abstractmethod

class UserRepositoryInterface(ABC):
    """
    Abstract interface for user storage. Implementations may use files, databases, etc.
    """

    @abstractmethod
    async def create_user(self, username: str, hashed_password: str, role: str = "player") -> bool:
        """
        Create a new user in storage.
        
        :param username: Unique username.
        :param hashed_password: The user's hashed password.
        :param role: User role (default is "player").
        :return: True if creation succeeded, False otherwise.
        """
        pass

    @abstractmethod
    async def get_user_hashed_password(self, username: str) -> Optional[str]:
        """
        Retrieve the hashed password for a given user.
        
        :param username: The username.
        :return: The hashed password if user exists, else None.
        """
        pass

    @abstractmethod
    async def user_exists(self, username: str) -> bool:
        """
        Check if a user exists.
        
        :param username: The username.
        :return: True if user exists, False otherwise.
        """
        pass

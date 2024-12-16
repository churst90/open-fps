# domain/ai/ai_entity.py
from typing import Tuple

class AIEntity:
    """
    Represents an AI-controlled entity in the game world.
    
    Attributes:
    - ai_id: A unique identifier for this AI entity.
    - name: The AI’s name or type.
    - position: A tuple (x, y, z) representing the entity's position.
    - health: Current health points.
    - speed: Movement speed.
    - role: Optional role or AI type (e.g., "guard", "monster").
    """

    def __init__(self, ai_id: str, name: str, position: Tuple[int, int, int], health: int, speed: float, role: str = "npc"):
        self.ai_id = ai_id
        self.name = name
        self.position = position
        self.health = health
        self.speed = speed
        self.role = role

    def move(self, dx: float, dy: float, dz: float):
        """
        Move the AI by the specified deltas.
        """
        x, y, z = self.position
        self.position = (x + dx, y + dy, z + dz)

    def update_health(self, amount: int):
        """
        Update the AI's health by a given amount.
        Ensure it does not fall below zero.
        """
        self.health = max(self.health + amount, 0)

    def to_dict(self) -> dict:
        return {
            "ai_id": self.ai_id,
            "name": self.name,
            "position": self.position,
            "health": self.health,
            "speed": self.speed,
            "role": self.role
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            ai_id=data["ai_id"],
            name=data["name"],
            position=tuple(data["position"]),
            health=data["health"],
            speed=data["speed"],
            role=data.get("role", "npc")
        )

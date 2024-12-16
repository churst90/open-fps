# domain/weather/weather_system.py
from typing import Dict, Any

class WeatherSystem:
    """
    Represents a weather system with certain conditions.
    Attributes:
    - weather_id: Unique ID for this weather system
    - condition: A string like "clear", "rain", "snow", etc.
    - intensity: A numeric value representing how strong the weather is
    - properties: Additional details (wind_speed, lightning, duration, etc.)
    """

    def __init__(self, weather_id: str, condition: str, intensity: float, properties: Dict[str, Any] = None):
        self.weather_id = weather_id
        self.condition = condition
        self.intensity = intensity
        self.properties = properties or {}

    def to_dict(self):
        return {
            "weather_id": self.weather_id,
            "condition": self.condition,
            "intensity": self.intensity,
            "properties": self.properties
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            weather_id=data["weather_id"],
            condition=data["condition"],
            intensity=data["intensity"],
            properties=data.get("properties", {})
        )

# game/weather_data.py
import logging
from typing import Dict, Any

class WeatherData:
    """
    Represents the local client's view of the current weather conditions.
    The server may send weather updates (e.g., condition: "rain", intensity, wind_speed, etc.).
    Store these and provide easy access for the UI or sound manager (e.g., play rain sounds).
    """

    def __init__(self):
        self.logger = logging.getLogger("WeatherData")
        self.current_weather: Dict[str, Any] = {
            "condition": "clear",
            "intensity": 0,
            "properties": {}
        }
        # "properties" could include wind_speed, wind_direction, temperature, humidity, etc.

    def update_weather(self, data: Dict[str, Any]):
        """
        Update current weather from a dictionary.
        data format might be:
        {
          "condition": str,
          "intensity": float/int,
          "properties": {...}
        }
        """
        self.current_weather = data
        self.logger.debug(f"Weather updated: {data['condition']}, intensity: {data['intensity']}.")

    def get_weather(self) -> Dict[str, Any]:
        """
        Return the current weather data.
        """
        return self.current_weather

    def clear_weather(self):
        """
        Reset weather to default clear state.
        """
        self.current_weather = {"condition": "clear", "intensity": 0, "properties": {}}
        self.logger.debug("Weather reset to clear.")

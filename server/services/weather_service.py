# services/weather_service.py
import logging
from typing import Optional
import uuid

from domain.weather.weather_system import WeatherSystem

class WeatherService:
    """
    Manages weather-related events:
    - "weather_start_request": start a certain weather condition
    - "weather_stop_request": stop a certain weather system
    - Possibly "weather_update_request": change intensity or properties

    The service uses a repository to persist or retrieve weather states if needed.
    """

    def __init__(self, event_dispatcher, weather_repository, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.weather_repository = weather_repository
        self.logger = logger or logging.getLogger("WeatherService")

    async def start(self):
        await self.event_dispatcher.subscribe("weather_start_request", self.handle_weather_start_request)
        await self.event_dispatcher.subscribe("weather_stop_request", self.handle_weather_stop_request)

    async def handle_weather_start_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "weather_start_request",
            "condition": <str>,
            "intensity": <float>,
            "properties": {...}
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]

        condition = msg.get("condition", "clear")
        intensity = msg.get("intensity", 0.0)
        properties = msg.get("properties", {})

        weather_id = str(uuid.uuid4())
        weather = WeatherSystem(weather_id, condition, intensity, properties)

        success = await self.weather_repository.save_weather(weather)
        if success:
            self.logger.info(f"Started weather '{condition}' with ID {weather_id}.")
            await self._ok("weather_start_ok", client_id, {"weather_id": weather_id, "condition": condition})
        else:
            await self._fail("weather_start_fail", client_id, "Failed to save weather state.")

    async def handle_weather_stop_request(self, event_data):
        """
        event_data = {
          "client_id": <str>,
          "message": {
            "message_type": "weather_stop_request",
            "weather_id": <str>
          }
        }
        """
        msg = event_data["message"]
        client_id = event_data["client_id"]
        weather_id = msg.get("weather_id")

        if not weather_id:
            await self._fail("weather_stop_fail", client_id, "Missing weather_id.")
            return

        if not await self.weather_repository.weather_exists(weather_id):
            await self._fail("weather_stop_fail", client_id, f"Weather '{weather_id}' does not exist.")
            return

        success = await self.weather_repository.remove_weather(weather_id)
        if success:
            self.logger.info(f"Stopped weather '{weather_id}'.")
            await self._ok("weather_stop_ok", client_id, {"weather_id": weather_id})
        else:
            await self._fail("weather_stop_fail", client_id, "Failed to remove weather.")

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

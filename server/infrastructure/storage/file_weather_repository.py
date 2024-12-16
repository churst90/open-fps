# infrastructure/storage/file_weather_repository.py
import aiofiles
import json
import os
import logging
from typing import Optional
from .weather_repository_interface import WeatherRepositoryInterface
from domain.weather.weather_system import WeatherSystem

class FileWeatherRepository(WeatherRepositoryInterface):
    def __init__(self, weather_dir: str = "weather", logger: Optional[logging.Logger] = None):
        self.weather_dir = weather_dir
        self.logger = logger or logging.getLogger("FileWeatherRepository")
        os.makedirs(self.weather_dir, exist_ok=True)

    def _weather_file_path(self, weather_id: str) -> str:
        return os.path.join(self.weather_dir, f"{weather_id}.json")

    async def save_weather(self, weather: WeatherSystem) -> bool:
        path = self._weather_file_path(weather.weather_id)
        data = weather.to_dict()
        try:
            async with aiofiles.open(path, "w") as f:
                await f.write(json.dumps(data, ensure_ascii=False))
            return True
        except Exception as e:
            self.logger.exception(f"Error saving weather '{weather.weather_id}': {e}")
            return False

    async def load_weather(self, weather_id: str) -> Optional[WeatherSystem]:
        path = self._weather_file_path(weather_id)
        if not os.path.exists(path):
            return None
        try:
            async with aiofiles.open(path, "r") as f:
                data = json.loads(await f.read())
            return WeatherSystem.from_dict(data)
        except Exception as e:
            self.logger.exception(f"Error loading weather '{weather_id}': {e}")
            return None

    async def remove_weather(self, weather_id: str) -> bool:
        path = self._weather_file_path(weather_id)
        if not os.path.exists(path):
            return False
        try:
            os.remove(path)
            return True
        except Exception as e:
            self.logger.exception(f"Error removing weather '{weather_id}': {e}")
            return False

    async def weather_exists(self, weather_id: str) -> bool:
        return os.path.exists(self._weather_file_path(weather_id))

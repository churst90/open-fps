# infrastructure/storage/weather_repository_interface.py
from abc import ABC, abstractmethod
from typing import Optional
from domain.weather.weather_system import WeatherSystem

class WeatherRepositoryInterface(ABC):
    @abstractmethod
    async def save_weather(self, weather: WeatherSystem) -> bool:
        pass

    @abstractmethod
    async def load_weather(self, weather_id: str) -> Optional[WeatherSystem]:
        pass

    @abstractmethod
    async def remove_weather(self, weather_id: str) -> bool:
        pass

    @abstractmethod
    async def weather_exists(self, weather_id: str) -> bool:
        pass

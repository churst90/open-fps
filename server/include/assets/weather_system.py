import random

class WeatherSystem:
    def __init__(self, condition='clear', position):
        self.condition = condition
        self.temperature = t60
        self.position = position
        self.conditions = {
            "clear": {"temperature": 75, "humidity": 30, "size": (0, 0, 0, 0, 0, 0), "wind_speed": 5, "wind_direction": "north"},
            "rain": {"temperature": 60, "humidity": 60, "size": (0, 0, 0, 0, 0, 0), "wind_speed": 5, "wind_direction": "north", "intensity": 3, "lightning": False, "thunder": False, "lifespan": 3},
            "snow": {"temperature": 20, "humidity": 60, "size": (0, 0, 0, 0, 0, 0), "wind_speed": 5, "wind_direction": "north", "intensity": 3, "lightning": False, "thunder": False, "lifespan": 3},
            "hurricane": {"temperature": 70, "humidity": 80, "size": (0, 0, 0, 0, 0, 0), "wind_speed": 75, "wind_direction": "north", "intensity": 8, "lightning": True, "thunder": True, "lifespan": 8}
        }
        self.map_bounds = None
        self.update_weather_properties()

    def update_weather_properties(self):
        properties = self.conditions.get(self.condition, {})
        for key, value in properties.items():
            setattr(self, key, value)

    def set_map_bounds(self, bounds):
        self.map_bounds = bounds

    def move_weather_system(self):
        # Example: move the system randomly within map bounds
        self.position = (self.position[0] + random.randint(-1, 1), self.position[1] + random.randint(-1, 1), self.position[2])
        # Here, you could dispatch an event to notify system movement

    def set_intensity(self, intensity):
        self.intensity = intensity
        self.wind_speed += intensity  # Simplified effect
        # Dispatch an intensity change event

    def blow_wind(self):
        self.wind_speed = random.randint(1, 20)  # Example gust simulation
        # Dispatch a wind speed change event

    def set_wind_direction(self, direction):
        self.wind_direction = direction
        # Dispatch a wind direction change event

    def flash_lightning(self):
        self.lightning = True
        # Dispatch a lightning flash event
        self.lightning = False

    def update_position(self, new_position):
        self.position = new_position
        # Dispatch a position change event

    def change_wind(self):
        self.wind_direction = random.choice(['N', 'S', 'E', 'W'])
        self.wind_speed = random.randint(1, 20) * self.intensity
        # Dispatch wind change events

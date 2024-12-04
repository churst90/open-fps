class WeatherManager:
    def __init__(self):
        self.weather_systems = {}

    def create_weather_system(self, name, system_type, intensity, wind_speed, temperature, pressure, bounds, direction):
        new_system = WeatherSystem(name, system_type, intensity, wind_speed, temperature, pressure, bounds, direction)
        self.weather_systems[new_system.id] = new_system

    def update_weather(self):
        for system_id, system in self.weather_systems.items():
            system.update()
            # Optional: Remove the system if it moves out of bounds or dissipates
            # if system_should_be_removed(system):
            #     del self.weather_systems[system_id]

    def get_weather_at(self, x, y, z):
        # Determine current weather at a specific location by checking if the point is within any weather system bounds
        for system in self.weather_systems.values():
            x1, y1, z1, x2, y2, z2 = system.bounds
            if x1 <= x <= x2 and y1 <= y <= y2 and z1 <= z <= z2:
                return system
        return WeatherSystem("Clear Sky")  # Return clear weather if no system is found at the location

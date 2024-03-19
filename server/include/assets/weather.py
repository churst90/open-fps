class Weather:
    def __init__(self, weather_type="clear", intensity=0, duration=0):
        self.type = weather_type
        self.intensity = intensity
        self.duration = duration

    def to_dict(self):
        return {
            "type": self.type,
            "intensity": self.intensity,
            "duration": self.duration
        }

    # Additional methods here for managing weather behavior

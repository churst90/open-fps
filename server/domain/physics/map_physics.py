# domain/physics/map_physics.py

class MapPhysics:
    """
    Represents the physics settings for a map.
    By default:
    - gravity: -9.8 (Earth gravity)
    - air_resistance: 0.0 (no air resistance by default)
    - friction: 0.0 (no friction by default, can be tuned)
    """
    def __init__(self, gravity: float = -9.8, air_resistance: float = 0.0, friction: float = 0.0):
        self.gravity = gravity
        self.air_resistance = air_resistance
        self.friction = friction

    def to_dict(self):
        return {
            "gravity": self.gravity,
            "air_resistance": self.air_resistance,
            "friction": self.friction
        }

    @classmethod
    def from_dict(cls, data: dict):
        return cls(
            gravity=data.get("gravity", -9.8),
            air_resistance=data.get("air_resistance", 0.0),
            friction=data.get("friction", 0.0)
        )

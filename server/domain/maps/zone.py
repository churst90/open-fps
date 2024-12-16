# domain/maps/zone.py
from typing import Tuple, Optional

class Zone:
    """
    Zones now use floats for their bounds.
    """

    def __init__(
        self, 
        zone_label: str,
        bounds: Tuple[float, float, float, float, float, float],
        is_safe: bool = False,
        is_hazard: bool = False,
        zone_type: str = "normal",
        destination_map: Optional[str] = None,
        destination_coords: Optional[Tuple[float,float,float]] = None
    ):
        # Convert bounds and destination_coords to float
        self.zone_label = zone_label
        self.bounds = tuple(float(v) for v in bounds)
        self.is_safe = is_safe
        self.is_hazard = is_hazard
        self.zone_type = zone_type
        self.destination_map = destination_map
        if destination_coords:
            self.destination_coords = tuple(float(v) for v in destination_coords)
        else:
            self.destination_coords = None

    def to_dict(self) -> dict:
        return {
            "zone_label": self.zone_label,
            "bounds": self.bounds,
            "is_safe": self.is_safe,
            "is_hazard": self.is_hazard,
            "zone_type": self.zone_type,
            "destination_map": self.destination_map,
            "destination_coords": self.destination_coords
        }

    @classmethod
    def from_dict(cls, data: dict):
        dest_coords = data.get("destination_coords")
        if dest_coords:
            dest_coords = tuple(float(v) for v in dest_coords)
        return cls(
            zone_label=data["zone_label"],
            bounds=tuple(float(v) for v in data["bounds"]),
            is_safe=data.get("is_safe", False),
            is_hazard=data.get("is_hazard", False),
            zone_type=data.get("zone_type", "normal"),
            destination_map=data.get("destination_map"),
            destination_coords=dest_coords
        )

    def contains_point(self, x: float, y: float, z: float) -> bool:
        x_min, x_max, y_min, y_max, z_min, z_max = self.bounds
        return (x_min <= x <= x_max) and (y_min <= y <= y_max) and (z_min <= z <= z_max)

import uuid

class Zone:
    def __init__(self, zone_label, zone_position, is_safe, is_hazard):
        self.zone_key = str(uuid.uuid4())  # Generate a unique identifier
        self.zone_label = zone_label
        self.zone_position = zone_position
        self.is_safe = is_safe
        self.is_hazard = is_hazard

    def set_zone_label(self, label):
        self.zone_label = label

    def set_zone_position(self, new_position):
        self.zone_position = new_position

    def to_dict(self):
        return {
            "zone_key": self.zone_key,
            "zone_label": self.zone_label,
            "zone_position": self.zone_position,
            "is_safe": self.is_safe,
            "is_hazard": self.is_hazard
        }

    @classmethod
    def from_dict(cls, data):
        return cls(
            zone_label = data['zone_label'],
            zone_position = data['zone_position'],
            is_safe = data['is_safe'],
            is_hazard = data['is_hazard']
        )
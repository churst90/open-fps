import uuid

class Zone:
    def __init__(self, zone_position, zone_type):
        self.zone_key = str(uuid.uuid4())  # Generate a unique identifier
        self.zone_position = zone_position  # Assuming zone_position is a tuple (x, y, z)
        self.zone_type = zone_type  # Could be an enum, string, or numeric identifier

    def update_zone_position(self, new_position):
        self.zone_position = new_position

    def update_zone_type(self, new_type):
        if new_type in ('air', 'brick', 'concrete', 'cement', 'cloth', 'dirt', 'glass', 'grass', 'sand', 'leaves', 'water'):
            self.zone_type = new_type
        else:
            print("invalid zone type")

    def to_dict(self):
        return {
            "zone_key": self.zone_key,
            "zone_position": self.zone_position,
            "zone_type": self.zone_type
        }

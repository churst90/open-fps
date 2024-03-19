import uuid

class Zone:
    def __init__(self, zone_label, zone_position, zone_type):
        self.zone_key = str(uuid.uuid4())  # Generate a unique identifier
        self.zone_label = zone_label
        self.zone_position = zone_position
        self.zone_type = zone_type

    def update_zone_label(self, label):
        self.zone_label = label

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
            "zone_label": self.zone_label,
            "zone_position": self.zone_position,
            "zone_type": self.zone_type
        }

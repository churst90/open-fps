class Position:
    def __init__(self, x=0, y=0, z=0):
        self.x = x
        self.y = y
        self.z = z

    def update(self, x=None, y=None, z=None):
        if x is not None:
            self.x = x
        if y is not None:
            self.y = y
        if z is not None:
            self.z = z

    def move_forward(self, distance):
        # Assuming forward is along the y-axis for example
        self.y += distance

    def move_backward(self, distance):
        self.y -= distance

    def move_left(self, distance):
        self.x -= distance

    def move_right(self, distance):
        self.x += distance

    def move_up(self, distance):
        self.z += distance

    def move_down(self, distance):
        self.z -= distance

    # Additional methods for other directions, validation, etc.

    def distance_to(self, other_position):
        return ((self.x - other_position.x)**2 + (self.y - other_position.y)**2 + (self.z - other_position.z)**2) ** 0.5

    def to_dict(self):
        return {'x': self.x, 'y': self.y, 'z': self.z}

    @classmethod
    def from_dict(cls, data):
        return cls(data['x'], data['y'], data['z'])

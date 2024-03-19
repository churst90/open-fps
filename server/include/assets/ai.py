class AI:
    def __init__(self, ai_id, name, position, health, speed):
        self.ai_id = ai_id
        self.name = name
        self.position = position  # Position should be a tuple (x, y, z)
        self.health = health
        self.speed = speed

    def move(self, direction, distance=None):
        """Move the AI in a given direction. Direction is a tuple (dx, dy, dz)."""
        if distance is None:
            distance = self.speed
        self.position = tuple(p + d * distance for p, d in zip(self.position, direction))

    def update_health(self, amount):
        """Update the AI's health by a given amount."""
        self.health += amount
        self.health = max(self.health, 0)  # Ensure health doesn't go below 0

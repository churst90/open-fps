class AIEntity:
    def __init__(self, ai_id, name, position, health, speed):
        self.ai_id = ai_id
        self.name = name
        self.position = position
        self.health = health
        self.speed = speed

    def move(self, new_position):
        # Basic movement logic, can be overridden for specific AI behaviors
        self.position = new_position

    def update(self):
        # Update AI state. This method should be overridden by subclasses to implement specific behavior patterns
        pass

    def on_player_detected(self, player_position):
        # Define how AI reacts to player detection, to be implemented by subclasses
        pass

class AIRegistry:
    def __init__(self):
        self.ai_entities = {}  # Stores AI entities by their ID
        self.map_ai = {}  # Maps each AI ID to a map name

    def create_ai(self, ai_id, name, position, health, speed, map_name):
        if ai_id in self.ai_entities:
            raise ValueError(f"AI with ID {ai_id} already exists.")
        ai_entity = AIEntity(ai_id, name, position, health, speed)
        self.ai_entities[ai_id] = ai_entity
        self.map_ai[ai_id] = map_name

    def remove_ai(self, ai_id):
        if ai_id in self.ai_entities:
            del self.ai_entities[ai_id]
            del self.map_ai[ai_id]

    def update_ai_position(self, ai_id, direction, distance=None):
        if ai_id in self.ai_entities:
            self.ai_entities[ai_id].move(direction, distance)

    def update_ai_health(self, ai_id, amount):
        if ai_id in self.ai_entities:
            self.ai_entities[ai_id].update_health(amount)

    def get_ai_by_map(self, map_name):
        """Get a list of AI entities on a specific map."""
        return [ai for ai_id, ai in self.ai_entities.items() if self.map_ai.get(ai_id) == map_name]


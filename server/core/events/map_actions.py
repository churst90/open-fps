import math
from core.events.event_handler import EventHandler

class MapActions(EventHandler):
    def __init__(self, network, user_registry, map_registry, event_dispatcher):
        super().__init__(network, event_dispatcher)
        self.map_registry = map_registry


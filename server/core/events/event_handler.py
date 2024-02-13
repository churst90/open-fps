class EventHandler:
    def __init__(self, network, maps):
        self.network = network
        self.maps = maps

    async def handle_event(self, event_type, data):
        raise NotImplementedError("This method should be overridden by subclasses.")

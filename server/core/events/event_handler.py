class EventHandler:
    def __init__(self, network, event_dispatcher):
        self.network = network
        self.event_dispatcher = event_dispatcher

    async def handle_event(self, event_type, data):
        raise NotImplementedError("This method should be overridden by subclasses.")

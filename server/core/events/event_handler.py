from .event_dispatcher import EventDispatcher

class EventHandler:
    def __init__(self, event_dispatcher):
        self.event_dispatcher = event_dispatcher

    async def emit_event(self, event_type, data):
        await self.event_dispatcher.emit(event_type, data)
        print("")

    async def handle_event(self, event_type, data):
        raise NotImplementedError("This method should be overridden by subclasses.")

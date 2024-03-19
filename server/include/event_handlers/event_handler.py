from .event_dispatcher import EventDispatcher

class EventHandler:
    def __init__(self, event_dispatcher):
        self.event_dispatcher = event_dispatcher

    async def emit_event(self, event_type, data, scope="global", map_id=None, recipient_username=None):
        # Adjusted to pass along the new parameters for scope-based dispatching
        await self.event_dispatcher.dispatch(event_type, data, scope, map_id, recipient_username)

    async def handle_event(self, event_type, data):
        raise NotImplementedError("This method should be overridden by subclasses.")

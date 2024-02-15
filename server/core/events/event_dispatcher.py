import asyncio
import json

class EventDispatcher:
    _instance = None  # Class variable to hold the singleton instance

    @classmethod
    def get_instance(cls):
        """
        A class method to get the singleton instance of EventDispatcher.
        If the instance doesn't exist, it creates it.
        """
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def __init__(self):
        """
        Constructor is private to prevent direct instantiation.
        """
        if EventDispatcher._instance is not None:
            raise RuntimeError("Use get_instance() method to get an instance of this class.")
        self.listeners = {}

    def subscribe(self, event_type, listener):
        """
        Subscribe a listener to a specific type of event.
        """
        if event_type not in self.listeners:
            self.listeners[event_type] = []
        self.listeners[event_type].append(listener)

    async def dispatch(self, event_type, data):
        """
        Dispatch an event to all subscribed listeners.
        """
        for listener in self.listeners.get(event_type, []):
            # Ensure that the listener can handle the event asynchronously
            if asyncio.iscoroutinefunction(listener):
                await listener(data)
            else:
                listener(data)

    def emit(self, event_type, data):
        """
        Schedule the dispatch coroutine on the existing asyncio event loop.
        This method allows for the dispatching of events from synchronous code.
        """
        loop = asyncio.get_event_loop()
        if loop.is_running():
            loop.create_task(self.dispatch(event_type, data))
        else:
            asyncio.run(self.dispatch(event_type, data))

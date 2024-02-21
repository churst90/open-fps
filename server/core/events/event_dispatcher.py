import asyncio
import json
import contextlib

class EventDispatcher:
    _instance = None

    @classmethod
    def get_instance(cls, network = None):
        if cls._instance is None:
            cls._instance = cls(network)
        return cls._instance

    def __init__(self, network):
        if EventDispatcher._instance is not None:
            raise RuntimeError("Use get_instance() method to get an instance of this class.")
        self.listeners = {}
        self.network = network

    def subscribe(self, event_type, listener):
        if event_type not in self.listeners:
            self.listeners[event_type] = []
        self.listeners[event_type].append(listener)

    async def dispatch(self, event_type, data):
        if data.get('scope') == 'private':
            writer = await self.network.get_writer(data.get('to'))
            if writer:
                await self.network.send(data, [writer])
                return  # Skip normal listener dispatch

        for listener in self.listeners.get(event_type, []):
            if asyncio.iscoroutinefunction(listener):
                await listener(data)
            else:
                listener(data)

    async def emit(self, event_type, data):
        loop = asyncio.get_event_loop()
        if loop.is_running():
            loop.create_task(self.dispatch(event_type, data))
        else:
            print("Event loop is not running. Event cannot be emitted.")

    def unsubscribe(self, event_type, listener):
        """
        Unsubscribe a listener from a specific type of event.
        
        Args:
            event_type (str): The type of event to unsubscribe from.
            listener (callable): The listener function to remove.
        """
        if event_type in self.listeners:
            with contextlib.suppress(ValueError):
                self.listeners[event_type].remove(listener)
            if not self.listeners[event_type]:
                # Optionally remove the event type if no listeners remain
                del self.listeners[event_type]

    async def update_network(self, network):
        self.network = network
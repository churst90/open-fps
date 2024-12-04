from collections import defaultdict

class EventDispatcher:
    _instance = None

    @classmethod
    def get_instance(cls, network=None, logger=None):
        """Get the singleton instance of the EventDispatcher."""
        if cls._instance is None:
            cls._instance = cls(network, logger)
        return cls._instance

    def __init__(self, network, logger):
        """Initialize the EventDispatcher."""
        if EventDispatcher._instance is not None:
            raise RuntimeError("Use get_instance() to access the EventDispatcher.")
        
        self.listeners = defaultdict(list)
        self.network = network
        self.logger = logger

    def subscribe(self, event_type, listener):
        """Subscribe a listener to an event type."""
        self.listeners[event_type].append(listener)
        self.logger.debug(f"Listener added for event: {event_type}. Total listeners: {len(self.listeners[event_type])}")

    def unsubscribe(self, event_type, listener):
        """Unsubscribe a listener from an event type."""
        if listener in self.listeners[event_type]:
            self.listeners[event_type].remove(listener)
            self.logger.debug(f"Listener removed for event: {event_type}. Remaining listeners: {len(self.listeners[event_type])}")
        else:
            self.logger.warning(f"Attempted to unsubscribe a listener that is not registered for event: {event_type}")

    async def dispatch(self, event_type, data):
        """Dispatch an event to all relevant listeners."""
        self.logger.info(f"Dispatching event: {event_type} with data: {data}")

        # Notify local listeners
        for listener in self.listeners.get(event_type, []):
            try:
                await listener(data)  # Assumes all listeners are async
                self.logger.debug(f"Listener for event: {event_type} successfully notified.")
            except Exception as e:
                self.logger.exception(f"Error while notifying listener for event: {event_type}")

        # Optionally, send to clients if necessary
        if self.network and hasattr(self.network, 'client_listeners') and event_type in self.network.client_listeners:
            try:
                await self.network.send_to_clients(event_type, data)
                self.logger.debug(f"Event: {event_type} dispatched to clients.")
            except Exception as e:
                self.logger.exception(f"Error while dispatching event: {event_type} to clients.")

    async def update_network(self, network):
        """Update the network instance for dispatching client events."""
        self.network = network
        self.logger.info("Network instance updated in EventDispatcher.")

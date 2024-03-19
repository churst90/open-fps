import asyncio
from collections import defaultdict

class EventDispatcher:
    _instance = None

    @classmethod
    def get_instance(cls, network=None):
        if cls._instance is None:
            cls._instance = cls(network)
        return cls._instance

    def __init__(self, network):
        if EventDispatcher._instance is not None:
            raise RuntimeError("Use get_instance() method to get an instance of this class.")
        self.internal_listeners = defaultdict(list)  # For server components
        self.client_listeners = defaultdict(list)  # For client-specific events
        self.user_to_map = {}
        self.network = network

    def subscribe_internal(self, event_type, listener):
        self.internal_listeners[event_type].append(listener)

    def unsubscribe_internal(self, event_type, listener):
        if listener in self.internal_listeners[event_type]:
            self.internal_listeners[event_type].remove(listener)

    def subscribe_client(self, event_type, username):
        self.client_listeners[event_type].append(username)

    def unsubscribe_client(self, event_type, username):
        if username in self.client_listeners[event_type]:
            self.client_listeners[event_type].remove(username)

    async def notify_listeners(self, event_type, data):
        # Notify internal listeners
        for listener in self.internal_listeners.get(event_type, []):
            await listener(data)  # Assuming internal listeners are synchronous for simplicity

        # Notify client listeners
        if event_type in self.client_listeners:
            writers = await self.network.get_writers_by_usernames(self.client_listeners[event_type])
            await self.network.send_to_writers(writers, data)

    # Public method to dispatch events both internally and to clients
    async def dispatch(self, event_type, data, scope="global", map_id=None, recipient_username=None):
        # Notify local server components
        await self.notify_listeners(event_type, data)

        # Determine the scope and dispatch to clients accordingly
        if scope == "global":
            await self.dispatch_global(data)
        elif scope == "map" and map_id:
            await self.dispatch_map_specific(map_id, data)
        elif scope == "private" and recipient_username:
            await self.dispatch_private(recipient_username, data)

    # Dispatch global events to all connected clients
    async def dispatch_global(self, data):
        writers = await self.network.get_all_writers()
        await self.network.send_to_writers(writers, data)

    # Dispatch map-specific events to clients on a particular map
    async def dispatch_map_specific(self, map_id, data):
        usernames = self.get_usernames_by_map(map_id)
        writers = await self.network.get_writers_by_usernames(usernames)
        await self.network.send_to_writers(writers, data)

    # Dispatch private messages to a specific client
    async def dispatch_private(self, recipient_username, data):
        writer = await self.network.get_writer(recipient_username)
        if writer:
            await self.network.send_to_writers([writer], data)

    async def update_user_map(self, username, map_id=None, action="add"):
        if action == "add" and map_id is not None:
            self.user_to_map[username] = map_id
        elif action == "remove":
            if username in self.user_to_map:
                del self.user_to_map[username]

    async def update_network(self, network):
        self.network = network

    def get_usernames_by_map(self, map_id):
        # Assuming `user_to_map` is now structured as map_id -> [usernames]
        return self.user_to_map.get(map_id, [])
import asyncio
from collections import defaultdict
import contextlib

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
        self.listeners = defaultdict(list)
        self.network = network

    async def dispatch(self, event_type, data):
        if 'scope' in data:
            await self.handle_chat_scope(data)
        else:
            for listener in self.listeners[event_type]:
                if asyncio.iscoroutinefunction(listener):
                    await listener(data)
                else:
                    listener(data)

    async def handle_chat_scope(self, data):
        scope = data.get('scope')
        if scope == 'private':
            await self.send_private_message(data)
        elif scope == 'map':
            await self.send_map_message(data)
        elif scope == 'global':
            await self.send_global_message(data)

    async def send_private_message(self, data):
        recipient = data.get('to')
        writer = await self.network.get_writer(recipient)
        if writer:
            await self.network.send(data, [writer])

    async def send_map_message(self, data):
        map_name = data.get('to')
        for username, user in self.network.user_reg.users.items():
            if user.current_map == map_name:
                writer = await self.network.get_writer(username)
                if writer:
                    await self.network.send(data, [writer])

    async def send_global_message(self, data):
        for username in self.network.user_reg.users:
            writer = await self.network.get_writer(username)
            if writer:
                await self.network.send(data, [writer])

    def subscribe(self, event_type, listener):
        self.listeners[event_type].append(listener)

    async def emit(self, event_type, data):
        loop = asyncio.get_event_loop()
        if loop.is_running():
            loop.create_task(self.dispatch(event_type, data))
        else:
            print("Event loop is not running. Event cannot be emitted.")

    def unsubscribe(self, event_type, listener):
        if event_type in self.listeners:
            with contextlib.suppress(ValueError):
                self.listeners[event_type].remove(listener)
            if not self.listeners[event_type]:
                del self.listeners[event_type]

    async def update_network(self, network):
        self.network = network

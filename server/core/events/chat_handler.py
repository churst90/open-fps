class ChatHandler:
    def __init__(self, event_dispatcher, user_registry, map_registry):
        self.event_dispatcher = event_dispatcher
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.event_dispatcher.subscribe_internal('chat_message', self.handle_chat_message)

    async def handle_chat_message(self, data):
        message_type = data.get("type")
        if message_type == "private":
            await self.handle_private_message(data)
        elif message_type == "map":
            await self.handle_map_message(data)
        elif message_type == "global":
            await self.handle_global_message(data)

    async def handle_private_message(self, data):
        # Implementation for handling private messages
        pass

    async def handle_map_message(self, data):
        # Implementation for handling map-specific messages
        pass

    async def handle_global_message(self, data):
        # Implementation for handling global messages
        pass

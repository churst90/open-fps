class ChatHandler(EventHandler):

    def __init__(self, user_registry, map_registry, event_dispatcher):
        super().__init__(event_dispatcher)
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.event_dispatcher = event_dispatcher
        self.event_dispatcher.subscribe("chat_message", self.handle_chat_message)

    async def handle_chat_message(self, data):
        message_type = data.get("type")
        if message_type == "private":
            await self.handle_private_message(data)
        elif message_type == "map":
            await self.handle_map_message(data)
        elif message_type == "global":
            await self.handle_global_message(data)

    async def handle_private_message(self, data):
        sender = data.get("sender")
        recipient = data.get("recipient")
        message = data.get("message")

        await self.emit_event("private_chat", {
            "scope": "private",
            "from": sender,
            "to": recipient,
            "message": message
            })

    async def handle_map_message(self, data):
        sender = data.get("sender")
        map_name = data.get("map_name")
        message = data.get("message")

        await self.emit_event("map_chat", {
            "scope": "map",
            "from": sender,
            "to": map_name,
            "message": message
                })

    async def handle_global_message(self, data):
        sender = data.get("sender")
        message = data.get("message")

        await self.emit_event("global_chat", {
            "type": "global",
            "from": sender,
            "message": message
            })


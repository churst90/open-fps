class ChatHandler(EventHandler):

    def __init__(self, network, event_dispatcher):
        super().__init__(event_dispatcher)
        self.network = network

    async def handle_chat(self, data):
        from_user = data.get('from')
        to = data.get('to')
        scope = data.get('scope')
        message = data.get('message')

        # Handle private chat
        if scope == 'private_chat':
            await self.handle_private_chat(from_user, to, message)
        
        # For map and global chats, emit events to be handled appropriately
        if scope in ['map', 'global']:
            event_name = f"{scope}_chat"
            self.emit_event(event_name, {
                "message_type": "chat_message",
                "scope": scope,
                "from": from_user,
                "to": to,
                "message": message
            })

    async def handle_private_chat(self, from_user, to, message):
        to_writer = await self.network.get_writer(to)
        from_writer = await self.network.get_writer(from_user)
        if to_writer and from_writer:
            await self.network.send({"message_type": "chat_message", "from": from_user, "message": message}, [to_writer])
            # Optionally echo back to the sender if needed
            # await self.network.send({"message_type": "chat_message", "from": from_user, "message": message}, [from_writer])
        else:
            print(f"Cannot send private message. User {to} or {from_user} not connected.")

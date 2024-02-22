class ClientHandler:
    def __init__(self, network):
        self.network = network  # Assume network is an instance of the Network class

    async def handle_login(self, username, password):
        if not self.network.is_connected:  # You might need to add an is_connected property to Network
            print("now attempting to connect to the server...")
            await self.network.connect()
        # Then send the login message
        login_message = {"message_type": "handle_login", "username": username, "password": password, "action": "login"}
        await self.network.send(login_message)

    async def start_handling(self):
        # Start the background task for handling incoming messages
        asyncio.create_task(self.network.handle_incoming_messages())

        # Additional logic for handling specific actions

    async def start_processing(self):
        await self.network.connect()
        asyncio.create_task(self.process_messages())

    async def process_messages(self):
        while True:
            message = await self.network.get_next_message()
            # Assume message is a dict with a 'type' key
            if message['type'] == 'login':
                await self.handle_login(message)

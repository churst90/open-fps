import asyncio

class ClientHandler:
    def __init__(self, network, loop):
        self.loop = loop
        self.network = network

    async def process_messages(self):
        print("Process messages method called ...")
        while True:
            message = await self.network.get_next_message()
            print(f"Received message: {message}")

    async def handle_login(self, username, password):
        login_message = {"message_type": "handle_login", "username": username, "password": password, "action": "login"}
        await self.network.connect()
        if self.network.connected_event.is_set():
            await self.network.send(login_message)
            asyncio.run_coroutine_threadsafe(self.process_messages(), self.loop)


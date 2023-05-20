import asyncio
import pickle

# Project specific
from clienthandler import ClientHandler

class Client:
    def __init__(self, client_handler, host, port):
        self.host = host
        self.port = port
        self.client_handler = client_handler

    async def connect(self):
        self.reader, self.writer = await asyncio.open_connection(self.host, self.port)

    async def disconnect(self):
        await self.writer.close()
        await self.writer.wait_closed()

    async def send(self, data):
        serialized_data = pickle.dumps(data)
        self.writer.write(serialized_data)
        await self.writer.drain()

    async def receive(self):
        try:
            while True:
                data = await self.reader.read(4096)
                if not data:
                    break
                data = pickle.loads(data)
                for message_type, message_handler in self.client_handler.messageTypes.items():
                    if data["type"] == message_type:
                        self.client_handler.messageTypes[message_type](data)

        except asyncio.CancelledError:
            pass

# Standard library imports
import json
import asyncio
import logging

class Network:
    def __init__(self, host, port, message_queue, message_handler):
        self.host = host
        self.port = port
        self.message_queue = message_queue
        self.message_handler = message_handler
        self.server = None
        self.logger = logging.getLogger('network')
        self.connections = {}  # Key: username, Value: (reader, writer)

    async def accept_connections(self):
        self.server = await asyncio.start_server(self.handle_client, self.host, self.port)
        self.logger.info(f"Listening for incoming connections on {self.host}:{self.port}")
        async with self.server:
            await self.server.serve_forever()

    async def handle_client(self, reader, writer):
        username = None  # Initialize username
        addr = writer.get_extra_info("peername")
        self.logger.info(f"Accepted connection from {addr}")
        # Track connections initially by address
        self.connections[addr] = (reader, writer)

        try:
            while True:
                data = await reader.read(1024)
                if not data:
                    break
                data = json.loads(data.decode('utf-8'))
                username = data.get('username')
                if username:
                    # Once username is known, use it as the main key and remove the address-based entry
                    self.connections[username] = self.connections.pop(addr)
                await self.message_queue.put((data, writer))
        except asyncio.CancelledError:
            self.logger.info("Connection handling cancelled")
        except Exception as e:
            self.logger.exception("An unexpected error occurred:", exc_info=e)
        finally:
            writer.close()
            await writer.wait_closed()
            remove_key = username if username in self.connections else addr
            if remove_key in self.connections:
                del self.connections[remove_key]
            self.logger.info(f"Connection closed: {remove_key}")

    async def send(self, message, client_sockets):
        if not isinstance(client_sockets, list):
            client_sockets = [client_sockets]

        # Serialize and send the message
        data = json.dumps(message)
        for client_socket in client_sockets:
            if client_socket and not client_socket.is_closing():
                try:
                    client_socket.write(data.encode('utf-8'))
                    await client_socket.drain()
                except Exception as e:
                    self.logger.exception(f"Failed to send message to {client_socket.get_extra_info('peername')}: {e}")

    async def process_received_data(self):
        while not self.message_queue.empty():
            data, writer = await self.message_queue.get()
            associated_user = None
            for username, (reader, stored_writer) in self.connections.items():
                if stored_writer == writer:
                    associated_user = username
                    break
            if associated_user is None:
                self.logger.warning("Received data from an unknown connection.")
                continue
            await self.message_handler(data, writer)

    async def close(self):
        self.logger.info("Closing server and all client connections")
        # Properly unpack the reader and writer from the tuple
        for reader, writer in self.connections.values():
            writer.close()
            await writer.wait_closed()
        if self.server:
            self.server.close()
            await self.server.wait_closed()

    def disconnect_client(self, username):
        if username in self.connections:
            writer = self.connections[username][1]
            writer.close()
            del self.connections[username]

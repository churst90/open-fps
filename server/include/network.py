import asyncio
import json
import ssl
from collections import defaultdict

class Network:
    _instance = None

    @classmethod
    def get_instance(cls, host, port, message_queue, message_handler, shutdown_event, logger, ssl_cert_file='keys/cert.pem', ssl_key_file='keys/key.pem'):
        """Singleton method to create or return the instance of the network."""
        if cls._instance is None:
            cls._instance = cls(host, port, message_queue, message_handler, shutdown_event, logger, ssl_cert_file, ssl_key_file)
        return cls._instance

    def __init__(self, host, port, message_queue, message_handler, shutdown_event, logger, ssl_cert_file='cert.pem', ssl_key_file='key.pem'):
        self.host = host
        self.port = port
        self.message_queue = message_queue
        self.message_handler = message_handler
        self.shutdown_event = shutdown_event
        self.logger = logger
        self.connections = {}  # Key: username, Value: (reader, writer)
        self.ssl_context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
        self.ssl_context.load_cert_chain(certfile=ssl_cert_file, keyfile=ssl_key_file)
        self.tasks = []  # Track all running tasks (e.g., connections)

    async def start(self):
        """Start the server to listen for incoming connections."""
        try:
            self.server = await asyncio.start_server(self.handle_client, self.host, self.port, ssl=self.ssl_context)
            self.logger.info(f"Listening on {self.host}:{self.port} with SSL/TLS")
            self.tasks.append(asyncio.create_task(self.server.serve_forever()))
        except Exception as e:
            self.logger.exception("Failed to start the network server")

    async def stop(self):
        """Stop the server and close all connections."""
        self.server.close()
        await self.server.wait_closed()

        # Close all client connections
        async with asyncio.Lock():
            for username, (reader, writer) in self.connections.items():
                writer.close()
                await writer.wait_closed()
                self.logger.info(f"Closed connection for user: {username}")
        self.connections.clear()

        # Cancel any remaining tasks
        for task in self.tasks:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                self.logger.debug("Cancelled a running network task")
        self.logger.info("Network server stopped.")

    async def handle_client(self, reader, writer):
        """Handle incoming client connections and process messages."""
        addr = writer.get_extra_info('peername')
        self.logger.info(f"Accepted connection from {addr}")

        try:
            while not reader.at_eof():
                data = await reader.read(1024)
                if data:
                    message = json.loads(data.decode('utf-8'))
                    username = message.get('username')

                    async with asyncio.Lock():
                        self.connections[username] = (reader, writer)
                    self.logger.debug(f"Received message from {username}: {message}")
                    await self.message_queue.put(message)
        except (asyncio.CancelledError, ConnectionResetError) as e:
            self.logger.warning(f"Connection error with {addr}: {e}")
        finally:
            # Clean up the connection when finished
            async with asyncio.Lock():
                for username, (client_reader, client_writer) in self.connections.items():
                    if client_writer == writer:
                        del self.connections[username]
                        self.logger.info(f"Disconnected user: {username}")
                        break
                writer.close()
                await writer.wait_closed()
            self.logger.info(f"Closed connection from {addr}")

    async def send(self, message, writer):
        """Send a message to a client."""
        if writer.is_closing():
            self.logger.warning("Tried to send a message to a closed connection.")
            return

        data = json.dumps(message)
        try:
            writer.write(data.encode('utf-8'))
            await writer.drain()
            self.logger.debug(f"Sent message to client: {data}")
        except Exception as e:
            self.logger.error(f"Failed to send message: {e}")

    async def process_received_data(self):
        """Process messages received from clients."""
        while not self.message_queue.empty():
            message = await self.message_queue.get()
            username = message.get('username')
            if username and username in self.connections:
                writer = self.connections[username][1]
                await self.message_handler(message, writer)

    async def get_all_writers(self):
        """Get all connected client writers."""
        async with asyncio.Lock():
            return [writer for reader, writer in self.connections.values()]

    async def get_writer(self, username):
        """Get a specific client's writer by their username."""
        async with asyncio.Lock():
            if username in self.connections:
                return self.connections[username][1]
            return None

    async def send_to_writers(self, writers, message):
        """Send a message to multiple client writers."""
        data = json.dumps(message).encode('utf-8')
        for writer in writers:
            if writer and not writer.is_closing():
                try:
                    writer.write(data)
                    await writer.drain()
                except Exception as e:
                    self.logger.warning(f"Failed to send message to writer: {e}")

    async def send_to_clients(self, event_type, data):
        """Send event-related messages to all clients listening to that event."""
        clients = await self.get_all_writers()
        await self.send_to_writers(clients, {"event_type": event_type, "data": data})

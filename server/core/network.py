# Standard library imports
import json
import asyncio
import logging
import ssl
from collections import defaultdict
from datetime import datetime, timedelta

class Network:
    _instance = None

    def __new__(cls, *args, **kwargs):
        if cls._instance is None:
            cls._instance = super(Network, cls).__new__(cls)
            # Initialize instance.
        return cls._instance

    @classmethod
    def get_instance(cls, host, port, message_queue, message_handler, shutdown_event, ssl_cert_file='cert.pem', ssl_key_file='key.pem'):
        if cls._instance is None:
            cls._instance = cls(host, port, message_queue, message_handler, shutdown_event, ssl_cert_file, ssl_key_file)
        return cls._instance

    def __init__(self, host, port, message_queue, message_handler, shutdown_event, ssl_cert_file='cert.pem', ssl_key_file='key.pem'):
        self.host = host
        self.port = port
        self.message_queue = message_queue
        self.message_handler = message_handler
        self.server = None
        self.logger = logging.getLogger('network')
        self.connections = {}  # Key: username, Value: (reader, writer)
        self.connection_attempts = defaultdict(int)  # Track connection attempts by IP
        self.last_connection_attempt = {}  # Timestamp of the last connection attempt by IP
        self.ssl_context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
        self.ssl_context.load_cert_chain(certfile='cert.pem', keyfile='key.pem')
        self.shutdown_event = shutdown_event

    async def start(self):
        self.listen_task = asyncio.create_task(self.accept_connections())
#        await asyncio.wait([self.listen_task], return_when=asyncio.FIRST_COMPLETED)

    async def stop(self):
        if self.listen_task:
            self.listen_task.cancel()  # Cancel the listening task
            try:
                await self.listen_task  # Wait for the task to be cancelled
            except asyncio.CancelledError:
                print("Network listening task cancelled.")
            self.listen_task = None  # Reset the task attribute

    async def accept_connections(self):
        # Start the server outside of the while loop to ensure it's created only once
        try:
            self.server = await asyncio.start_server(
                self.handle_client, self.host, self.port, ssl=self.ssl_context
            )
            print(f"Listening for incoming connections on {self.host}:{self.port} with SSL/TLS encryption")
            async with self.server:
                # Instead of a while loop checking the event, use serve_forever
                # and wait for the shutdown_event in a separate task.
                server_task = asyncio.create_task(self.server.serve_forever())
            
                # Wait for the shutdown_event to be set before stopping the server.
                await self.shutdown_event.wait()
            
                # Once shutdown_event is set, we cancel the server task.
                server_task.cancel()
                try:
                    await server_task  # Attempt to await the task to catch any cancellation
                except asyncio.CancelledError:
                    print("Server has stopped accepting new connections.")
        except Exception as e:
            print(f"Failed to start server: {e}")

    async def handle_client(self, reader, writer):
        addr = writer.get_extra_info("peername")
        now = datetime.now()

        # Rate limiting: Allow only 3 attempts per minute per IP
        if addr[0] in self.last_connection_attempt:
            if now - self.last_connection_attempt[addr[0]] < timedelta(minutes=1):
                self.connection_attempts[addr[0]] += 1
                if self.connection_attempts[addr[0]] > 3:
                    writer.close()
                    await writer.wait_closed()
                    self.logger.warning(f"Rate limit exceeded for {addr}")
                    return
            else:
                self.connection_attempts[addr[0]] = 1
        self.last_connection_attempt[addr[0]] = now

        print(f"Accepted connection from {addr} with SSL/TLS encryption")
        # Track connections initially by address
        self.connections[addr] = (reader, writer)
        username = None

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
            print("Connection handling cancelled")
        except Exception as e:
            self.logger.exception("An unexpected error occurred:", exc_info=e)
        finally:
            writer.close()
            await writer.wait_closed()
            remove_key = username if username in self.connections else addr
            if remove_key in self.connections:
                del self.connections[remove_key]
            print(f"Connection closed for {remove_key}")

    async def send(self, message, client_sockets):
        if not isinstance(client_sockets, list):
            client_sockets = [client_sockets]

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
        print("Closing server and all client connections")
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

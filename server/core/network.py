# Standard library imports
import json
import asyncio
from asyncio import Lock
import logging
import ssl
from collections import defaultdict
from datetime import datetime, timedelta

class Network:
    _lock = Lock()
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
        self.connections_lock = asyncio.Lock()  # Add a lock for managing connections

    async def start(self):
        self.server = await asyncio.start_server(
            self.handle_client, self.host, self.port, ssl=self.ssl_context
        )
        print(f"Listening for incoming connections on {self.host}:{self.port} with SSL/TLS encryption")
        asyncio.create_task(self.server.serve_forever())

    async def stop(self):
        self.server.close()
        await self.server.wait_closed()
        async with self.connections_lock:
            for _, writer in self.connections.values():
                writer.close()
        self.connections.clear()

    async def handle_client(self, reader, writer):
        addr = writer.get_extra_info("peername")
        self.logger.info(f"Accepted connection from {addr}")

        async with self.connections_lock:
            self.connections[addr] = (reader, writer)

        try:
            while not reader.at_eof():
                data = await reader.read(1024)
                if data:
                    message = json.loads(data.decode('utf-8'))
                    await self.message_queue.put(message)
        except asyncio.CancelledError:
            self.logger.info("Connection handling cancelled")
        except (OSError, ConnectionResetError) as e:
            self.logger.warning(f"Network error with {addr}: {e}")
        finally:
            async with self.connections_lock:
                writer.close()
                if addr in self.connections:
                    del self.connections[addr]
            self.logger.info(f"Connection closed for {addr}")

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

    async def disconnect_client(self, username):
        async with Network._lock:
            if username in self.connections:
                writer = self.connections[username][1]
                writer.close()
                del self.connections[username]

    async def get_writer(self, username):
        async with self.connections_lock:
            return self.connections.get(username, None)[1] if username in self.connections else None
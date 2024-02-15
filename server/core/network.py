# Standard library imports
import json
import asyncio
import logging
import ssl
from collections import defaultdict
from datetime import datetime, timedelta

class Network:
    def __init__(self, host, port, message_queue, message_handler, ssl_cert_file='cert.pem', ssl_key_file='key.pem'):
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
        self.ssl_context.load_cert_chain(certfile=ssl_cert_file, keyfile=ssl_key_file)

    async def accept_connections(self):
        self.server = await asyncio.start_server(self.handle_client, self.host, self.port, ssl=self.ssl_context)
        self.logger.info(f"Listening for incoming connections on {self.host}:{self.port} with SSL/TLS encryption")
        async with self.server:
            await self.server.serve_forever()

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

        self.logger.info(f"Accepted connection from {addr} with SSL/TLS encryption")
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
            self.logger.info("Connection handling cancelled")
        except Exception as e:
            self.logger.exception("An unexpected error occurred:", exc_info=e)
        finally:
            writer.close()
            await writer.wait_closed()
            remove_key = username if username in self.connections else addr
            if remove_key in self.connections:
                del self.connections[remove_key]
            self.logger.info(f"Connection closed for {remove_key}")

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
        self.logger.info("Closing server and all client connections")
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
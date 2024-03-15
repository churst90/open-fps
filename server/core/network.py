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
        self.banned_ips = set()
        self.ssl_context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
        self.ssl_context.load_cert_chain(certfile='cert.pem', keyfile='key.pem')
        self.shutdown_event = shutdown_event
        self.connections_lock = asyncio.Lock()  # Add a lock for managing connections

    async def start(self):
        self.server = await asyncio.start_server(
            self.handle_client, self.host, self.port
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
        print(f"Accepted connection from {addr}")
        ip = addr[0]
        if ip in self.banned_ips:
            print(f"Rejected connection from banned IP: {ip}")
            writer.close()  # Close the connection
            return

        # Track connection attempts
        self.connection_attempts[ip] += 1
        if self.connection_attempts[ip] > MAX_CONNECTION_ATTEMPTS:
            self.ban_ip(ip)
            print(f"Connection attempt limit exceeded. Banned IP: {ip}")
            writer.close()
            return

        # Reset connection attempts count after successful connection
        self.connection_attempts[ip] = 0

        try:
            while not reader.at_eof():
                data = await reader.read(1024)
                if data:
                    message = json.loads(data.decode('utf-8'))
                    username = message.get('username')
                    async with self.connections_lock:
                        self.connections[username] = (reader, writer, addr)
                    await self.message_queue.put(message)
        except asyncio.CancelledError:
            print("Connection handling cancelled")
        except (OSError, ConnectionResetError) as e:
            print(f"Network error with {addr}: {e}")
        finally:
            async with self.connections_lock:
                for username, conn_writer in self.connections.items():
                    if conn_writer == writer:
                        del self.connections[username]
                        break
                writer.close()
            print(f"Connection closed for {addr}")

    async def send_to_writers(self, writers, data):
        """Send data to multiple writers."""
        encoded_data = json.dumps(data).encode('utf-8')
        for writer in writers:
            if writer and not writer.is_closing():
                try:
                    writer.write(encoded_data)
                    await writer.drain()
                except Exception as e:
                    self.logger.exception(f"Failed to send message to a client: {e}")

    async def send(self, message, client_writer):
        if client_writer is None or client_writer.is_closing():
            self.logger.error("Attempted to send a message to a closed or nonexistent connection.")
            return
        data = json.dumps(message)
        try:
            client_writer.write(data.encode('utf-8'))
            await client_writer.drain()
            print("data sent to client")
        except Exception as e:
            self.logger.exception(f"Failed to send message to {client_writer.get_extra_info('peername')}: {e}")

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

    async def get_writers_by_usernames(self, usernames):
        async with self.connections_lock:
            return [writer for username, (reader, writer) in self.connections.items() if username in usernames]

    async def get_all_writers(self):
        async with self.connections_lock:
            return [client['writer'] for client in self.connections.values()]

    async def get_writers_by_map(self, usernames):
        async with self.connections_lock:
            return [self.get_writer_by_username(username) for username in usernames if username in self.connections]

    async def get_private_writers(self, sender_id, recipient_id):
        async with self.connections_lock:
            sender_writer = self.connections.get(sender_id, {}).get('writer')
            recipient_writer = self.connections.get(recipient_id, {}).get('writer')
            return [sender_writer, recipient_writer]

    async def get_writer(self, username):
        async with self.connections_lock:
            connection_tuple = self.connections.get(username)
        if connection_tuple is not None:
            # Assuming connection_tuple is a tuple where the writer is the second element
            _, writer = connection_tuple
            return writer
        else:
            # Username not found in connections, or connections tuple is malformed
            return None

    def ban_ip(self, ip):
        self.banned_ips.add(ip)
        print(f"Banned IP: {ip}")

    def unban_ip(self, ip):
        self.banned_ips.discard(ip)
        print(f"Unbanned IP: {ip}")

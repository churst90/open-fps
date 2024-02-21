import asyncio
import json
import ssl
import logging
from asyncio import IncompleteReadError

class Network:
    def __init__(self, logger):
        self.host = None
        self.port = None
        self.ssl_context = ssl.create_default_context(purpose=ssl.Purpose.CLIENT_AUTH)
        self.reader = None
        self.writer = None
        self.logger = logger
        self.retry_limit = 3
        self.retry_delay = 1  # Initial delay in seconds
        self.max_retry_delay = 60  # Maximum delay in seconds
        self.message_queue = asyncio.Queue()
        self.is_connected = False

    async def get_next_message(self):
        return await self.message_queue.get()

    async def handle_incoming_messages(self):
        while True:
            try:
                data = await self.receive()
                await self.message_queue.put(data)
            except Exception as e:
                self.logger.error(f"Error handling incoming message: {e}")
                break

    async def connect(self):
        attempt_count = 0
        delay = self.retry_delay
        while attempt_count < self.retry_limit:
            try:
                print(f"Address: {self.host} and port {self.port}")
                self.reader, self.writer = await asyncio.wait_for(asyncio.open_connection(self.host, self.port, ssl=self.ssl_context), timeout=10.0)
#                print(f"Connected to the server at {self.host}:{self.port} with encryption")
                self.is_connected = True
                return
            except (asyncio.TimeoutError, ConnectionRefusedError) as e:
                self.logger.warning(f"Connection attempt {attempt_count + 1} to {self.host}:{self.port} failed: {e}")
                await asyncio.sleep(delay)
                delay = min(delay * 2, self.max_retry_delay)  # Exponential backoff
            except Exception as e:
                self.logger.error(f"Failed to connect to the server: {e}")
                break  # Break on non-retryable errors
            attempt_count += 1
        raise ConnectionError(f"Could not connect to the server after {self.retry_limit} attempts.")

    async def disconnect(self):
        if self.writer:
            try:
                self.writer.close()
                await self.writer.wait_closed()
                self.logger.info("Disconnected from the server")
                self.is_connected = False
            except Exception as e:
                self.logger.error(f"Error occurred while disconnecting: {e}")

    async def send(self, data):
        try:
            serialized_data = json.dumps(data).encode('utf-8')
            self.writer.write(serialized_data)
            await self.writer.drain()
        except Exception as e:
            self.logger.error(f"Failed to send data: {e}")
            raise

    async def receive(self):
        try:
            data = await asyncio.wait_for(self.reader.read(4096), timeout=30.0)
            if data:
                return json.loads(data.decode('utf-8'))
            else:
                raise RuntimeError("Connection closed by server")
        except (json.JSONDecodeError, IncompleteReadError) as e:
            self.logger.error(f"Error processing received data: {e}")
        except asyncio.TimeoutError:
            self.logger.warning("Receiving data timed out")
        except Exception as e:
            self.logger.error(f"Failed to receive data: {e}")
            raise

    def set_host(self, host):
        self.host = host

    def set_port(self, port):
        self.port = port


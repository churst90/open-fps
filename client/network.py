import asyncio
import json
import ssl
import logging
from asyncio import IncompleteReadError, TimeoutError

class Network:
    def __init__(self, logger, loop):
        self.loop = loop
        self.connected_event = asyncio.Event()
        self.host = None
        self.port = None
#        self.ssl_context = ssl.create_default_context(purpose=ssl.Purpose.CLIENT_AUTH)
#        self.ssl_context.check_hostname = False
#        self.ssl_context.verify_mode = ssl.CERT_NONE
        self.reader, self.writer = None, None
        self.logger = logger
        self.retry_limit = 3
        self.retry_delay = 1
        self.max_retry_delay = 60
        self.message_queue = asyncio.Queue()

    async def get_next_message(self):
        print("Awaiting next message from queue")
        return await self.message_queue.get()

    async def handle_incoming_messages(self):
        self.logger.info("Starting to handle incoming messages")
        while self.connected_event.is_set():
            try:
                data = await self.receive()
                await self.message_queue.put(data)
            except Exception as e:
                self.logger.error(f"Error handling incoming message: {e}")
                break

    async def connect(self):
        for attempt in range(1, self.retry_limit + 1):
            self.logger.info(f"Connection attempt {attempt}")
            try:
                self.reader, self.writer = await asyncio.wait_for(
                    asyncio.open_connection(self.host, self.port),
                    timeout=10.0
                )
                self.connected_event.set()
                asyncio.run_coroutine_threadsafe(self.handle_incoming_messages(), self.loop)
                self.logger.info("Successfully connected to the server")
                return
            except (TimeoutError, ConnectionRefusedError, Exception) as e:
                self.logger.warning(f"Connection attempt {attempt} failed: {e}")
                if attempt < self.retry_limit:
                    await asyncio.sleep(self.retry_delay * attempt)
                else:
                    self.logger.error("Failed to connect after all attempts")
                    raise ConnectionError("Could not connect to the server after all attempts.")

    async def disconnect(self):
        if self.writer:
            try:
                self.writer.close()
                await self.writer.wait_closed()
                self.logger.info("Disconnected from the server")
            except Exception as e:
                self.logger.error(f"Error occurred while disconnecting: {e}")

    async def send(self, data):
        if not self.connected_event.is_set():
            self.logger.warning("Attempted to send data without a connection")
            return
        try:
            serialized_data = json.dumps(data).encode('utf-8')
            self.writer.write(serialized_data)
            await self.writer.drain()
            self.logger.debug(f"Sent data: {data}")
        except Exception as e:
            self.logger.error(f"Failed to send data: {e}")
            raise

    async def receive(self):
        try:
            data = await self.reader.read(4096)
            if data:
                return json.loads(data.decode('utf-8'))
            else:
                raise RuntimeError("Connection closed by server")
        except (json.JSONDecodeError, IncompleteReadError, TimeoutError) as e:
            self.logger.error(f"Error processing received data: {e}")
            raise
        except Exception as e:
            self.logger.error(f"Unexpected error while receiving data: {e}")
            raise

    def set_host(self, host):
        self.host = host

    def set_port(self, port):
        self.port = port

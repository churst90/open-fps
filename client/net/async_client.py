# net/async_client.py
import asyncio
import json
import logging
import ssl
from typing import Optional, Dict, Any

class AsyncClient:
    """
    An asynchronous TCP/SSL client that connects to the game server.
    Sends and receives JSON messages.
    """

    def __init__(self, host: str, port: int, use_ssl: bool = True, ssl_certfile: Optional[str] = None):
        self.logger = logging.getLogger("AsyncClient")
        self.host = host
        self.port = port
        self.use_ssl = use_ssl
        self.ssl_certfile = ssl_certfile
        self.reader = None
        self.writer = None

    async def connect(self):
        """
        Connect to the server. If use_ssl is True, wrap the connection in SSL.
        """
        ssl_context = None
        if self.use_ssl:
            ssl_context = ssl.create_default_context(ssl.Purpose.SERVER_AUTH)
            if self.ssl_certfile:
                ssl_context.load_verify_locations(self.ssl_certfile)
            ssl_context.check_hostname = False
            ssl_context.verify_mode = ssl.CERT_NONE

        self.logger.info(f"Connecting to {self.host}:{self.port} with SSL={self.use_ssl}")
        self.reader, self.writer = await asyncio.open_connection(self.host, self.port, ssl=ssl_context)
        self.logger.info("Connected to server.")

    async def send_message(self, message: Dict[str, Any]):
        """
        Send a JSON message to the server.
        """
        if not self.writer:
            self.logger.warning("Not connected, cannot send message.")
            return
        data = json.dumps(message) + "\n"
        self.writer.write(data.encode('utf-8'))
        await self.writer.drain()
        self.logger.debug(f"Sent message: {message}")

    async def receive_message(self) -> Optional[Dict[str, Any]]:
        """
        Read a single JSON message from the server.
        Messages are newline-delimited JSON objects.
        """
        if not self.reader:
            self.logger.warning("Not connected, cannot receive message.")
            return None

        line = await self.reader.readline()
        if not line:
            self.logger.warning("Connection closed by server.")
            return None
        line_str = line.decode('utf-8').strip()
        if not line_str:
            return None  # empty line, ignore

        try:
            msg = json.loads(line_str)
            self.logger.debug(f"Received message: {msg}")
            return msg
        except json.JSONDecodeError as e:
            self.logger.error(f"Failed to decode message: {line_str}, error: {e}")
            return None

    async def close(self):
        """
        Close the connection.
        """
        if self.writer:
            self.writer.close()
            await self.writer.wait_closed()
            self.logger.info("Connection closed.")

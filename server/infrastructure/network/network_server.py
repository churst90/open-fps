# infrastructure/network/network_server.py
import asyncio
import json
import ssl
import logging
from typing import Callable, Awaitable, Optional
from infrastructure.logging.custom_logger import get_logger

class NetworkServer:
    """
    NetworkServer handles TCP connections with optional SSL.
    It reads JSON lines from clients and passes them to a message_handler.
    Integrates with ConnectionManager for disconnect handling.

    Logging is used to track connections, disconnections, and message processing.
    """

    def __init__(
        self,
        host: str,
        port: int,
        message_handler: Callable[[dict, str], Awaitable[None]],
        ssl_context: Optional[ssl.SSLContext],
        connection_manager,
        logger: Optional[logging.Logger] = None
    ):
        """
        :param host: Host to bind to.
        :param port: Port to listen on.
        :param message_handler: Async callback for incoming messages.
        :param ssl_context: SSLContext or None for no SSL.
        :param connection_manager: ConnectionManager instance.
        :param logger: Optional custom logger.
        """
        self.host = host
        self.port = port
        self.message_handler = message_handler
        self.ssl_context = ssl_context
        self.connection_manager = connection_manager
        self.logger = logger or get_logger("NetworkServer", debug_mode=False)
        self.server: Optional[asyncio.AbstractServer] = None
        self.clients = {}
        self._shutdown_event = asyncio.Event()

    async def start(self) -> None:
        protocol = "with SSL/TLS" if self.ssl_context else "without SSL"
        self.logger.info(f"Starting NetworkServer on {self.host}:{self.port} {protocol}.")
        self.server = await asyncio.start_server(
            self._handle_new_connection,
            self.host,
            self.port,
            ssl=self.ssl_context
        )
        self.logger.info("NetworkServer started and accepting connections.")

    async def stop(self) -> None:
        self.logger.info("Stopping NetworkServer...")
        if self.server is not None:
            self.server.close()
            await self.server.wait_closed()

        # Close all connected clients
        for client_id, (_, writer) in self.clients.items():
            self.logger.debug(f"Closing connection for '{client_id}'.")
            writer.close()
            await writer.wait_closed()

        self.clients.clear()
        self.logger.info("NetworkServer stopped.")

    async def _handle_new_connection(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        # Generate a client_id
        client_addr = writer.get_extra_info('peername')
        if client_addr:
            client_id = f"{client_addr[0]}:{client_addr[1]}"
        else:
            import uuid
            client_id = str(uuid.uuid4())

        self.logger.info(f"New client connected: client_id='{client_id}'.")
        self.clients[client_id] = (reader, writer)

        try:
            await self._read_from_client(client_id, reader, writer)
        except asyncio.CancelledError:
            self.logger.warning(f"Client task cancelled: client_id='{client_id}'.")
        except Exception as e:
            self.logger.exception(f"Unexpected error in client task '{client_id}': {e}")
        finally:
            self.logger.info(f"Client '{client_id}' disconnected.")
            # Notify connection_manager
            await self.connection_manager.handle_disconnect(client_id)

            if client_id in self.clients:
                del self.clients[client_id]
            writer.close()
            await writer.wait_closed()

    async def _read_from_client(self, client_id: str, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        while True:
            line = await reader.readline()
            if not line:
                # Client closed connection.
                self.logger.debug(f"No more data from client_id='{client_id}', assuming disconnect.")
                break

            line_str = line.decode('utf-8').strip()
            if not line_str:
                self.logger.debug(f"Empty line received from client_id='{client_id}', ignoring.")
                continue

            self.logger.debug(f"Received data from client_id='{client_id}': {line_str}")
            try:
                message = json.loads(line_str)
                await self.message_handler(message, client_id)
            except json.JSONDecodeError:
                self.logger.warning(f"Invalid JSON from client_id='{client_id}': {line_str}")
            except Exception as e:
                self.logger.exception(f"Error handling message from client_id='{client_id}': {e}")

    async def send_message(self, client_id: str, message: dict) -> bool:
        if client_id not in self.clients:
            self.logger.warning(f"Cannot send message to '{client_id}', not connected.")
            return False

        _, writer = self.clients[client_id]
        if writer.is_closing():
            self.logger.warning(f"Writer for '{client_id}' is closing, cannot send message.")
            return False

        data = (json.dumps(message) + "\n").encode('utf-8')
        self.logger.debug(f"Sending data to client_id='{client_id}': {message}")
        try:
            writer.write(data)
            await writer.drain()
            return True
        except Exception as e:
            self.logger.exception(f"Failed to send message to '{client_id}': {e}")
            return False

    def get_connected_clients(self) -> list:
        connected = list(self.clients.keys())
        self.logger.debug(f"get_connected_clients() -> {connected}")
        return connected

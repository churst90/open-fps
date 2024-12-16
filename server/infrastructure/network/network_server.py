import asyncio
import json
import ssl
import logging
from typing import Callable, Awaitable, Optional

class NetworkServer:
    """
    The NetworkServer class manages client connections over TCP with SSL/TLS,
    reads incoming JSON messages, and forwards them to a handler callback.
    It does not contain business logic—just networking.

    Integrates with ConnectionManager to handle disconnects.
    """

    def __init__(
        self,
        host: str,
        port: int,
        message_handler: Callable[[dict, str], Awaitable[None]],
        ssl_context: Optional[ssl.SSLContext],
        connection_manager,  # new parameter
        logger: Optional[logging.Logger] = None
    ):
        """
        Initialize the NetworkServer.

        :param host: Host to bind the server to.
        :param port: Port to listen on.
        :param message_handler: An async callback to process incoming messages.
        :param ssl_context: SSLContext with cert/key if using SSL, or None if no SSL.
        :param connection_manager: The ConnectionManager instance for handling mappings.
        :param logger: Optional logger, or one will be created.
        """
        self.host = host
        self.port = port
        self.message_handler = message_handler
        self.ssl_context = ssl_context
        self.connection_manager = connection_manager
        self.logger = logger or logging.getLogger("NetworkServer")
        self.server: Optional[asyncio.AbstractServer] = None
        self.clients = {}
        self._shutdown_event = asyncio.Event()

    async def start(self) -> None:
        protocol = "with SSL/TLS" if self.ssl_context else "without SSL"
        self.logger.info(f"Starting NetworkServer on {self.host}:{self.port} {protocol}")
        self.server = await asyncio.start_server(
            self._handle_new_connection,
            self.host,
            self.port,
            ssl=self.ssl_context
        )
        self.logger.info("NetworkServer started and listening.")

    async def stop(self) -> None:
        self.logger.info("Stopping NetworkServer...")
        if self.server is not None:
            self.server.close()
            await self.server.wait_closed()

        # Close all client connections
        for client_id, (_, writer) in self.clients.items():
            self.logger.debug(f"Closing connection for client {client_id}")
            writer.close()
            await writer.wait_closed()

        self.clients.clear()
        self.logger.info("NetworkServer stopped.")

    async def _handle_new_connection(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        client_addr = writer.get_extra_info('peername')
        if client_addr:
            client_id = f"{client_addr[0]}:{client_addr[1]}"
        else:
            import uuid
            client_id = str(uuid.uuid4())

        self.logger.info(f"New connection from {client_id}")
        self.clients[client_id] = (reader, writer)

        try:
            await self._read_from_client(client_id, reader, writer)
        except asyncio.CancelledError:
            self.logger.warning(f"Task cancelled for client {client_id}")
        except Exception as e:
            self.logger.exception(f"Error in client task {client_id}: {e}")
        finally:
            self.logger.info(f"Client {client_id} disconnected.")
            # Handle disconnect in connection_manager
            await self.connection_manager.handle_disconnect(client_id)

            if client_id in self.clients:
                del self.clients[client_id]
            writer.close()
            await writer.wait_closed()

    async def _read_from_client(self, client_id: str, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        while True:
            line = await reader.readline()
            if not line:
                # Client closed connection
                break

            line_str = line.decode('utf-8').strip()
            if not line_str:
                continue

            try:
                message = json.loads(line_str)
                await self.message_handler(message, client_id)
            except json.JSONDecodeError:
                self.logger.warning(f"Invalid JSON from client {client_id}: {line_str}")
            except Exception as e:
                self.logger.exception(f"Error handling message from {client_id}: {e}")

    async def send_message(self, client_id: str, message: dict) -> bool:
        if client_id not in self.clients:
            self.logger.warning(f"Cannot send to {client_id}, not connected.")
            return False

        _, writer = self.clients[client_id]
        if writer.is_closing():
            self.logger.warning(f"Writer for {client_id} is closing.")
            return False

        data = (json.dumps(message) + "\n").encode('utf-8')
        try:
            writer.write(data)
            await writer.drain()
            return True
        except Exception as e:
            self.logger.exception(f"Failed to send message to {client_id}: {e}")
            return False

    def get_connected_clients(self) -> list:
        return list(self.clients.keys())

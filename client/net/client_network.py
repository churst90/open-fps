# net/client_network.py
import asyncio
import logging
from typing import Optional, Dict, Any
from net.async_client import AsyncClient
from net.message_schemas import MESSAGE_TYPE_TO_SCHEMA
from pydantic import ValidationError

class ClientNetwork:
    """
    Async-friendly network interface.
    - start() connects and starts receive_loop as a background task.
    - get_incoming_message() is async and returns messages or None if disconnected.
    - validate_message() uses schemas to validate messages.
    """

    def __init__(self, host: str, port: int, use_ssl: bool = True, ssl_certfile: Optional[str] = None):
        self.logger = logging.getLogger("ClientNetwork")
        self.client = AsyncClient(host, port, use_ssl, ssl_certfile)
        self.running = False
        self.incoming_queue = asyncio.Queue()
        self.receive_task = None

    async def start(self):
        await self.client.connect()
        self.running = True
        self.receive_task = asyncio.create_task(self._receive_loop())
        self.logger.info("ClientNetwork started and receiving messages.")

    async def stop(self):
        self.running = False
        if self.receive_task:
            self.receive_task.cancel()
            try:
                await self.receive_task
            except asyncio.CancelledError:
                pass
        await self.client.close()
        self.logger.info("ClientNetwork stopped.")

    async def _receive_loop(self):
        self.logger.info("Started receive loop.")
        while self.running:
            msg = await self.client.receive_message()
            if msg is None:
                self.logger.warning("No message or server disconnected.")
                self.running = False
                break
            await self.incoming_queue.put(msg)
        self.logger.info("Receive loop ended.")

    async def send_request(self, message: Dict[str, Any]):
        if not self.running:
            self.logger.warning("Not running, cannot send request.")
            return
        await self.client.send_message(message)

    async def get_incoming_message(self) -> Optional[Dict[str, Any]]:
        if not self.running and self.incoming_queue.empty():
            return None
        try:
            return await self.incoming_queue.get()
        except asyncio.CancelledError:
            return None

    async def validate_message(self, msg: Dict[str, Any]) -> Optional[Any]:
        if "message" not in msg or "client_id" not in msg:
            self.logger.warning("Invalid message format (missing client_id or message field).")
            return None

        inner = msg["message"]
        message_type = inner.get("message_type")
        schema = MESSAGE_TYPE_TO_SCHEMA.get(message_type)
        if not schema:
            self.logger.debug(f"No schema for {message_type}, treating as unknown.")
            return None

        try:
            validated = schema(**inner)
            return validated
        except ValidationError as e:
            self.logger.error(f"Validation error for {message_type}: {e}")
            return None

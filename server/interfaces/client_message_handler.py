import logging
from typing import Dict, Any, Optional, Callable, Awaitable
from schemas import MESSAGE_TYPE_TO_SCHEMA
from pydantic import ValidationError

class ClientMessageHandler:
    """
    The ClientMessageHandler acts as a translator between raw network messages 
    (received as dictionaries) and the event system. It examines each message, 
    identifies what type of event it corresponds to, and uses the event dispatcher 
    to trigger that event.

    This allows a clean separation: the NetworkServer knows nothing about game logic, 
    and the various services don't need to know about raw network I/O formats.
    """

    def __init__(
        self, 
        event_dispatcher, 
        logger: Optional[logging.Logger] = None,
        default_event_type: str = "unknown_message"
    ):
        """
        Initialize the ClientMessageHandler.

        :param event_dispatcher: An instance of EventDispatcher for dispatching events.
        :param logger: An optional logger. If not provided, a default logger will be created.
        :param default_event_type: The event type to use if we cannot determine one from the message.
        """
        self.logger = logger or logging.getLogger("ClientMessageHandler")
        self.event_dispatcher = event_dispatcher
        self.default_event_type = default_event_type

        # A registry of message_type -> event_type mappings.
        # You can populate this dictionary as your application grows.
        self.message_type_map = {
            # Example: "login_request" -> "user_login_request"
            # These can be filled in as you define your game protocols.
        }

        # Optional: Allow custom per-message-type validation or processing.
        # Example: "login_request": self._validate_login_request
        self.message_validators: Dict[str, Callable[[Dict[str, Any]], bool]] = {}

    async def handle_message(self, message: dict, client_id: str):
        # Check base structure first
        if "message" not in message or "client_id" not in message:
            await self.event_dispatcher.dispatch("invalid_message", {
                "client_id": client_id,
                "message": {"reason": "Missing client_id or message field"}
            })
            return

        if "message_type" not in message["message"]:
            await self.event_dispatcher.dispatch("invalid_message", {
                "client_id": client_id,
                "message": {"reason": "Missing message_type in message"}
            })
            return

        message_type = message["message"]["message_type"]
        schema = MESSAGE_TYPE_TO_SCHEMA.get(message_type)
        if not schema:
            await self.event_dispatcher.dispatch("invalid_message", {
                "client_id": client_id,
                "message": {"reason": f"Unknown message_type '{message_type}'"}
            })
            return

        # Validate with pydantic
        try:
            # This validates the entire structure including message_type
            validated = schema(**message["message"])
            # Replace message["message"] with validated dict to ensure only valid fields
            message["message"] = validated.dict()
        except ValidationError as e:
            self.logger.warning(f"Validation error for {client_id}: {e.errors()}")
            await self.event_dispatcher.dispatch("invalid_message", {
                "client_id": client_id,
                "message": {"reason": "Validation failed", "errors": e.errors()}
            })
            return

        # If we get here, validation passed
        event_type = message_type
        await self.event_dispatcher.dispatch(event_type, message)

    async def _dispatch_event(self, event_type: str, message: Dict[str, Any], client_id: str) -> None:
        """
        A helper method to dispatch an event with added client context.

        :param event_type: The determined event type for this message.
        :param message: The original message dictionary.
        :param client_id: The identifier of the client who sent the message.
        """
        # Include client_id in the event data so that listeners know who triggered it.
        event_data = {
            "client_id": client_id,
            "message": message
        }

        self.logger.debug(f"Dispatching event '{event_type}' for message from {client_id}.")
        await self.event_dispatcher.dispatch(event_type, event_data)

    def register_message_type(
        self,
        message_type: str,
        event_type: str,
        validator: Optional[Callable[[Dict[str, Any]], bool]] = None
    ) -> None:
        """
        Register a new message_type -> event_type mapping, optionally with a validator.

        :param message_type: The message type as defined by the client protocol.
        :param event_type: The event type that should be dispatched for this message type.
        :param validator: An optional callable that takes the message dict and returns True if valid.
        """
        self.message_type_map[message_type] = event_type
        if validator:
            self.message_validators[message_type] = validator
        self.logger.debug(f"Registered message type '{message_type}' to event '{event_type}', validator: {bool(validator)}.")

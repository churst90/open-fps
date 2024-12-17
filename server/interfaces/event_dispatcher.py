# interfaces/event_dispatcher.py
import asyncio
import logging
from typing import Callable, Awaitable, Dict, List, Any

from infrastructure.logging.custom_logger import get_logger

class EventDispatcher:
    """
    The EventDispatcher provides a centralized way to manage events and their listeners.
    Components (services, handlers) register callbacks for certain event types.
    When an event is dispatched, the dispatcher calls all subscribed callbacks.

    This helps to decouple different parts of the system. For example, 
    when a message is received from the network, an event can be dispatched.
    Multiple services can listen and respond to that event without needing 
    direct references to each other.
    """

    def __init__(self, logger: logging.Logger = None):
        """
        Initialize the EventDispatcher.

        :param logger: Optional logger instance. If not provided, a default logger using custom_logger is created.
        """
        self.logger = logger or get_logger("EventDispatcher", debug_mode=False)
        self._listeners: Dict[str, List[Callable[[Dict[str, Any]], Awaitable[None]]]] = {}
        self._lock = asyncio.Lock()
        self.logger.debug("EventDispatcher initialized.")

    async def subscribe(self, event_type: str, listener: Callable[[Dict[str, Any]], Awaitable[None]]) -> None:
        """
        Subscribe a listener to an event type.

        :param event_type: The name of the event to listen for.
        :param listener: An async callable taking a single dictionary (event data) as a parameter.
        """
        async with self._lock:
            if event_type not in self._listeners:
                self._listeners[event_type] = []
            self._listeners[event_type].append(listener)
            self.logger.debug(f"Subscribed listener '{listener.__name__}' to event '{event_type}'.")

    async def unsubscribe(self, event_type: str, listener: Callable[[Dict[str, Any]], Awaitable[None]]) -> None:
        """
        Unsubscribe a listener from an event type.

        :param event_type: The name of the event.
        :param listener: The listener function to remove.
        """
        async with self._lock:
            if event_type in self._listeners and listener in self._listeners[event_type]:
                self._listeners[event_type].remove(listener)
                self.logger.debug(f"Unsubscribed listener '{listener.__name__}' from event '{event_type}'.")
            else:
                self.logger.warning(
                    f"Attempted to unsubscribe a listener '{listener.__name__}' that is not registered for event '{event_type}'."
                )

    async def dispatch(self, event_type: str, event_data: Dict[str, Any]) -> None:
        """
        Dispatch an event to all subscribed listeners.

        :param event_type: The name of the event to dispatch.
        :param event_data: A dictionary containing event data.
        """
        self.logger.info(f"Dispatching event '{event_type}' with data: {event_data}")

        # Copy the listeners list to avoid modification issues during iteration.
        async with self._lock:
            listeners = self._listeners.get(event_type, [])[:]

        # Notify listeners concurrently but handle errors individually.
        results = await asyncio.gather(
            *[self._notify_listener(listener, event_type, event_data) for listener in listeners],
            return_exceptions=True
        )

        for res in results:
            if isinstance(res, Exception):
                self.logger.debug(f"An exception was returned for event '{event_type}', listener notification failed gracefully.")

    async def _notify_listener(
        self,
        listener: Callable[[Dict[str, Any]], Awaitable[None]], 
        event_type: str, 
        event_data: Dict[str, Any]
    ) -> None:
        """
        Notify a single listener about an event, handling exceptions gracefully.

        :param listener: The listener callback.
        :param event_type: The event type being dispatched.
        :param event_data: The event data dictionary.
        """
        try:
            await listener(event_data)
            self.logger.debug(f"Listener '{listener.__name__}' handled event '{event_type}' successfully.")
        except Exception as e:
            self.logger.exception(f"Error notifying listener '{listener.__name__}' for event '{event_type}': {e}")

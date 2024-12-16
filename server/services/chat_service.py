# services/chat_service.py
import logging
from typing import Optional

class ChatService:
    """
    Handles chat messages of various categories:
    - private: sender -> recipient only
    - map: sender and all users on the same map
    - global: all users
    - server: all users (from server/admin)
    """

    def __init__(self, event_dispatcher, user_service, map_service, role_manager, chat_logger, connection_manager, logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.user_service = user_service
        self.map_service = map_service
        self.role_manager = role_manager
        self.chat_logger = chat_logger
        self.connection_manager = connection_manager
        self.logger = logger or logging.getLogger("ChatService")

    async def start(self):
        await self.event_dispatcher.subscribe("chat_message", self.handle_chat_message)

    async def handle_chat_message(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        chat_category = msg.get("chat_category")
        text = msg.get("text","")
        recipient = msg.get("recipient")
        map_name = msg.get("map_name")

        # We assume the user is authenticated and authorized for simplicity

        # Route message based on category
        if chat_category == "private":
            if recipient:
                recipient_client_id = await self.connection_manager.get_client_id_by_username(recipient)
                if recipient_client_id:
                    await self._send_chat_to_clients([recipient_client_id], username, text, chat_category, map_name, recipient)
                else:
                    self.logger.info(f"Recipient '{recipient}' not online.")
        elif chat_category == "map":
            # get all users in that map
            users_in_map = await self.map_service.get_users_in_map(map_name)
            client_ids = []
            for u in users_in_map:
                cid = await self.connection_manager.get_client_id_by_username(u.username)
                if cid:
                    client_ids.append(cid)
            await self._send_chat_to_clients(client_ids, username, text, chat_category, map_name)
        elif chat_category == "global":
            # All online users
            all_online_users = await self.user_service.get_logged_in_usernames()
            client_ids = []
            for u in all_online_users:
                cid = await self.connection_manager.get_client_id_by_username(u)
                if cid:
                    client_ids.append(cid)
            await self._send_chat_to_clients(client_ids, username, text, chat_category)
        elif chat_category == "server":
            # server messages go to all users
            all_online_users = await self.user_service.get_logged_in_usernames()
            client_ids = []
            for u in all_online_users:
                cid = await self.connection_manager.get_client_id_by_username(u)
                if cid:
                    client_ids.append(cid)
            await self._send_chat_to_clients(client_ids, "server", text, chat_category)
        else:
            self.logger.warning(f"Unknown chat category {chat_category} from {username}")

        # Log the message
        self.chat_logger.log_message(chat_category, username if username else "server", text, recipient, map_name)

    async def _send_chat_to_clients(self, client_ids, sender, text, chat_category, map_name=None, recipient=None):
        # For each client_id, dispatch an event "chat_receive"
        # In a real scenario, we might rely on a network service or a send_message event.
        for cid in client_ids:
            await self.event_dispatcher.dispatch("chat_receive", {
                "client_id": cid,
                "message": {
                    "chat_category": chat_category,
                    "sender": sender,
                    "text": text,
                    "recipient": recipient,
                    "map_name": map_name
                }
            })

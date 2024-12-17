# services/chat_service.py
import logging
from typing import Optional
from infrastructure.logging.custom_logger import get_logger

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
        self.logger = logger or get_logger("ChatService", debug_mode=False)
        self.logger.debug("ChatService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("chat_message", self.handle_chat_message)
        self.logger.info("ChatService started and subscribed to 'chat_message' event.")

    async def handle_chat_message(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        chat_category = msg.get("chat_category")
        text = msg.get("text", "")
        recipient = msg.get("recipient")
        map_name = msg.get("map_name")

        self.logger.debug(
            f"handle_chat_message called: client_id='{client_id}', username='{username}', "
            f"chat_category='{chat_category}', text_len={len(text)}, recipient='{recipient}', map_name='{map_name}'"
        )

        # For now, we assume the user is authenticated and authorized already.
        # In a real scenario, we might check user token/permissions here.

        if chat_category not in ["private", "map", "global", "server"]:
            self.logger.warning(f"Unknown chat category '{chat_category}' from user='{username}'.")
            return

        self.logger.debug(f"Processing '{chat_category}' chat from user='{username}'.")

        if chat_category == "private":
            if recipient:
                recipient_client_id = await self.connection_manager.get_client_id_by_username(recipient)
                if recipient_client_id:
                    self.logger.info(f"Sending private message from '{username}' to '{recipient}'.")
                    await self._send_chat_to_clients([recipient_client_id], username, text, chat_category, map_name, recipient)
                else:
                    self.logger.info(f"Private message intended for '{recipient}', but recipient not online.")
            else:
                self.logger.warning(f"No recipient provided for private message from user='{username}'.")

        elif chat_category == "map":
            if not map_name:
                self.logger.warning(f"Map chat with no map_name specified by user='{username}'.")
                return
            users_in_map = await self.map_service.get_users_in_map(map_name)
            client_ids = []
            self.logger.debug(f"Found {len(users_in_map)} users in map '{map_name}' for map chat.")
            for u in users_in_map:
                cid = await self.connection_manager.get_client_id_by_username(u.username)
                if cid:
                    client_ids.append(cid)
            if client_ids:
                self.logger.info(f"Sending map message from '{username}' to {len(client_ids)} users in map '{map_name}'.")
            else:
                self.logger.debug(f"No online users found in map '{map_name}' for this map message.")
            await self._send_chat_to_clients(client_ids, username, text, chat_category, map_name)

        elif chat_category == "global":
            all_online_users = await self.user_service.get_logged_in_usernames()
            client_ids = []
            self.logger.debug(f"Found {len(all_online_users)} online users for global chat.")
            for u in all_online_users:
                cid = await self.connection_manager.get_client_id_by_username(u)
                if cid:
                    client_ids.append(cid)
            if client_ids:
                self.logger.info(f"Sending global message from '{username}' to all {len(client_ids)} online users.")
            else:
                self.logger.debug("No online users found for global message.")
            await self._send_chat_to_clients(client_ids, username, text, chat_category)

        elif chat_category == "server":
            # server messages go to all users
            all_online_users = await self.user_service.get_logged_in_usernames()
            client_ids = []
            self.logger.debug(f"Server message: {len(all_online_users)} online users to notify.")
            for u in all_online_users:
                cid = await self.connection_manager.get_client_id_by_username(u)
                if cid:
                    client_ids.append(cid)
            self.logger.info(f"Broadcasting server message to {len(client_ids)} users.")
            await self._send_chat_to_clients(client_ids, "server", text, chat_category)

        # Log the message to the chat logs
        sender_for_log = username if username else "server"
        self.chat_logger.log_message(chat_category, sender_for_log, text, recipient, map_name)
        self.logger.debug(f"Chat message logged: category='{chat_category}', sender='{sender_for_log}', recipient='{recipient}', map='{map_name}'.")

    async def _send_chat_to_clients(self, client_ids, sender, text, chat_category, map_name=None, recipient=None):
        self.logger.debug(
            f"_send_chat_to_clients: sender='{sender}', chat_category='{chat_category}', "
            f"map_name='{map_name}', recipient='{recipient}', targets={len(client_ids)}"
        )
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
        if client_ids:
            self.logger.debug(f"Sent chat message to {len(client_ids)} clients.")
        else:
            self.logger.debug("No clients to send the chat message to.")

# infrastructure/storage/chat_logger.py
import logging
import os
from datetime import datetime
from pathlib import Path

class ChatLogger:
    """
    Logs chat messages to different files based on category.
    Files:
    - global_chat.log for global messages
    - server_chat.log for server messages
    - private_chat.log for private messages
    - {map_name}_chat.log for map messages
    """

    def __init__(self, logs_dir="chat_logs"):
        self.logs_dir = Path(logs_dir)
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        # We can keep separate loggers or just open files manually. Let's just open on demand.

    def log_message(self, chat_category: str, sender: str, text: str, recipient: str = None, map_name: str = None):
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if chat_category == "global":
            filename = "global_chat.log"
        elif chat_category == "server":
            filename = "server_chat.log"
        elif chat_category == "private":
            filename = "private_chat.log"
        elif chat_category == "map":
            if not map_name:
                # If no map_name provided, fallback to unknown_map_chat.log
                filename = "unknown_map_chat.log"
            else:
                filename = f"{map_name}_chat.log"
        else:
            filename = "unknown_chat.log"

        log_file = self.logs_dir / filename
        with log_file.open("a", encoding="utf-8") as f:
            if chat_category == "private":
                line = f"[{timestamp}] PRIVATE from {sender} to {recipient}: {text}\n"
            elif chat_category == "map":
                line = f"[{timestamp}] MAP {map_name} from {sender}: {text}\n"
            elif chat_category == "global":
                line = f"[{timestamp}] GLOBAL from {sender}: {text}\n"
            elif chat_category == "server":
                line = f"[{timestamp}] SERVER ANNOUNCE: {text}\n"
            else:
                line = f"[{timestamp}] {chat_category.upper()} from {sender}: {text}\n"
            f.write(line)

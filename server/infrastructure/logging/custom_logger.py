# infrastructure/logging/custom_logger.py
import logging
import os
import json
from logging.handlers import RotatingFileHandler

class JSONFormatter(logging.Formatter):
    def format(self, record):
        log_record = {
            "time": self.formatTime(record),
            "level": record.levelname,
            "name": record.name,
            "message": record.getMessage()
        }
        return json.dumps(log_record)

class SimpleConsoleFormatter(logging.Formatter):
    def format(self, record):
        # For WARNING and ERROR, prepend level name
        if record.levelno >= logging.WARNING:
            return f"{record.levelname}: {record.getMessage()}"
        # For INFO and DEBUG, just print the message without level prefix
        return f"{record.getMessage()}"

def get_logger(name: str, debug_mode: bool = False, log_file: str = 'app.log'):
    logger = logging.getLogger(name)
    # Remove existing handlers if any (to prevent duplication)
    logger.handlers = []
    logger.propagate = False

    logger.setLevel(logging.DEBUG if debug_mode else logging.INFO)

    # Ensure logs directory exists
    if not os.path.exists('logs'):
        os.makedirs('logs')

    file_handler = RotatingFileHandler(f'logs/{log_file}', maxBytes=1048576, backupCount=5)
    file_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)
    file_formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    file_handler.setFormatter(file_formatter)

    console_handler = logging.StreamHandler()
    console_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)
    # Use our SimpleConsoleFormatter
    console_handler.setFormatter(SimpleConsoleFormatter())

    json_handler = RotatingFileHandler(f'logs/{log_file}.json', maxBytes=1048576, backupCount=5)
    json_handler.setFormatter(JSONFormatter())
    json_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)

    logger.addHandler(file_handler)
    logger.addHandler(console_handler)
    logger.addHandler(json_handler)

    return logger

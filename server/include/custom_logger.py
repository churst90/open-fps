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

class CustomLogger:
    def __init__(self, name, debug_mode=False, log_file='app.log', debug_level=0):
        self.logger = logging.getLogger(name)
        self.debug_mode = debug_mode
        self.log_file = log_file
        self.debug_level = debug_level
        self.setup_logger()

    def setup_logger(self):
        """Sets up the logger with a file handler and a console handler."""
        self.logger.setLevel(logging.DEBUG)  # Always log everything, control is done via handlers

        # Create a rotating file handler
        if not os.path.exists('logs'):
            os.makedirs('logs')
        file_handler = RotatingFileHandler(f'logs/{self.log_file}', maxBytes=1048576, backupCount=5)
        file_handler.setLevel(logging.DEBUG)

        # Create console handler with a higher log level
        console_handler = logging.StreamHandler()
        console_handler.setLevel(logging.DEBUG)

        # Create formatter and add it to the handlers
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        file_handler.setFormatter(formatter)
        console_handler.setFormatter(formatter)

        # Optional JSON formatter for structured logging
        json_handler = RotatingFileHandler(f'logs/{self.log_file}.json', maxBytes=1048576, backupCount=5)
        json_handler.setFormatter(JSONFormatter())
        json_handler.setLevel(logging.DEBUG)

        # Add the handlers to the logger
        self.logger.addHandler(file_handler)
        self.logger.addHandler(console_handler)
        self.logger.addHandler(json_handler)

    def set_log_level(self, level: int):
        """Dynamically set the logger's log level."""
        self.logger.setLevel(level)

    def info_with_context(self, msg, context=None):
        """Logs a message with additional context."""
        self.logger.info(msg, extra={'context': context})

    def log_at_level(self, level, msg):
        """Logs a message at a specific debug level."""
        if level <= self.debug_level:
            self.logger.debug(f"DEBUG{level}: {msg}")

    def exception(self, msg):
        """Logs an exception with traceback."""
        self.logger.exception(msg)

    def toggle_debug_mode(self, enable: bool):
        """Enables or disables debug mode."""
        self.debug_mode = enable
        for handler in self.logger.handlers:
            handler.setLevel(logging.DEBUG if enable else logging.INFO)

    def info(self, msg):
        """Logs a message with level INFO."""
        self.logger.info(msg)

    def debug(self, msg):
        """Logs a message with level DEBUG."""
        self.logger.debug(msg)

    def warning(self, msg):
        """Logs a message with level WARNING."""
        self.logger.warning(msg)

    def error(self, msg):
        """Logs a message with level ERROR."""
        self.logger.error(msg)

    def critical(self, msg):
        """Logs a message with level CRITICAL."""
        self.logger.critical(msg)

def get_logger(name, debug_mode=False):
    """Convenience function to get a preconfigured CustomLogger instance."""
    return CustomLogger(name, debug_mode)

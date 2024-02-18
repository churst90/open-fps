import logging
from logging.handlers import RotatingFileHandler
import os

class CustomLogger:
    def __init__(self, name, debug_mode=False, log_file='app.log', debug_level=0):
        self.logger = logging.getLogger(name)
        self.debug_mode = debug_mode
        self.log_file = log_file
        self.debug_level = debug_level  # Add debug level attribute
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
        formatter = logging.Formatter('%(name)s - %(message)s')
        file_handler.setFormatter(formatter)
        console_handler.setFormatter(formatter)

        # Add the handlers to the logger
        self.logger.addHandler(file_handler)
        self.logger.addHandler(console_handler)

    def set_debug_level(self, level: int):
        """Sets the current debug level for the logger."""
        self.debug_level = level

    def log_at_level(self, level, msg):
        """Logs a message at a specific debug level."""
        if level <= self.debug_level:
            self.logger.debug(f"DEBUG{level}: {msg}")

    def debug_1(self, msg):
        """Logs a debug level 1 message."""
        self.log_at_level(1, msg)

    def debug_2(self, msg):
        """Logs a debug level 2 message."""
        self.log_at_level(2, msg)

    # You can add more debug level methods as needed

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

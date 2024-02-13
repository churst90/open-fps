import logging
from logging.handlers import RotatingFileHandler
import os

class CustomLogger:
    def __init__(self, name, debug_mode=False, log_file='app.log'):
        self.logger = logging.getLogger(name)
        self.debug_mode = debug_mode
        self.log_file = log_file
        self.setup_logger()

    def setup_logger(self):
        """Sets up the logger with a file handler and a console handler."""
        self.logger.setLevel(logging.DEBUG)  # Set root logger to debug, handlers control actual level

        # Create a rotating file handler
        if not os.path.exists('logs'):
            os.makedirs('logs')
        file_handler = RotatingFileHandler(f'logs/{self.log_file}', maxBytes=1048576, backupCount=5)
        file_handler.setLevel(logging.DEBUG if self.debug_mode else logging.INFO)

        # Create console handler with a higher log level
        console_handler = logging.StreamHandler()
        console_handler.setLevel(logging.DEBUG if self.debug_mode else logging.INFO)

        # Create formatter and add it to the handlers
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        file_handler.setFormatter(formatter)
        console_handler.setFormatter(formatter)

        # Add the handlers to the logger
        self.logger.addHandler(file_handler)
        self.logger.addHandler(console_handler)

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

# utils/logger.py
import logging
import os
from logging.handlers import RotatingFileHandler

def setup_logger():
    """
    Configure global logging for the client.
    This sets up both a console handler and a rotating file handler.
    """
    # Create a directory for logs if it doesn’t exist
    if not os.path.exists('logs'):
        os.makedirs('logs')

    logger = logging.getLogger()
    logger.setLevel(logging.DEBUG)  # or INFO, depending on the verbosity needed

    # Console handler for quick feedback
    console_handler = logging.StreamHandler()
    console_handler.setLevel(logging.INFO)
    console_formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    console_handler.setFormatter(console_formatter)

    # Rotating file handler to keep logs manageable
    file_handler = RotatingFileHandler('logs/client.log', maxBytes=1_000_000, backupCount=5)
    file_handler.setLevel(logging.DEBUG)
    file_formatter = logging.Formatter('%(asctime)s [%(levelname)s] %(name)s: %(message)s')
    file_handler.setFormatter(file_formatter)

    # Add handlers to the root logger
    logger.addHandler(console_handler)
    logger.addHandler(file_handler)

    logging.getLogger("ClientMain").info("Logger initialized successfully.")

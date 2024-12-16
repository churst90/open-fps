# include/custom_logger.py
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

def get_logger(name: str, debug_mode: bool = False, log_file: str = 'app.log'):
    logger = logging.getLogger(name)
    logger.setLevel(logging.DEBUG if debug_mode else logging.INFO)

    # Ensure logs directory exists
    if not os.path.exists('logs'):
        os.makedirs('logs')

    file_handler = RotatingFileHandler(f'logs/{log_file}', maxBytes=1048576, backupCount=5)
    file_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)
    formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    file_handler.setFormatter(formatter)

    console_handler = logging.StreamHandler()
    console_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)
    console_handler.setFormatter(formatter)

    json_handler = RotatingFileHandler(f'logs/{log_file}.json', maxBytes=1048576, backupCount=5)
    json_handler.setFormatter(JSONFormatter())
    json_handler.setLevel(logging.DEBUG if debug_mode else logging.INFO)

    # Avoid adding multiple handlers if logger already configured
    if not logger.handlers:
        logger.addHandler(file_handler)
        logger.addHandler(console_handler)
        logger.addHandler(json_handler)

    return logger

# net/message_handler.py
import logging
import asyncio
from typing import Any
from net.client_network import ClientNetwork
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager

class MessageHandler:
    """
    Asynchronous message handler:
    - run() is an async method that runs in a background task.
    - Continuously get messages from client_network.
    - Validate and handle them without blocking.
    - Use tts.speak() without blocking.
    """

    def __init__(self, client_network: ClientNetwork, tts: TTSManager, sound: SoundManager, game_state: dict):
        self.logger = logging.getLogger("MessageHandler")
        self.client_network = client_network
        self.tts = tts
        self.sound = sound
        self.game_state = game_state
        self.running = True

    async def run(self):
        self.logger.info("MessageHandler started.")
        while self.running:
            msg = await self.client_network.get_incoming_message()
            if msg is None:
                # Check if network is running or no more messages
                if not self.client_network.running:
                    break
                await asyncio.sleep(0.1)
                continue

            validated = await self.client_network.validate_message(msg)
            if validated is None:
                self.logger.debug("Unknown or invalid message ignored.")
                continue

            # Handle message async
            await self.handle_message(validated)

        self.logger.info("MessageHandler stopped.")

    async def handle_message(self, validated: Any):
        message_type = validated.message_type
        self.logger.debug(f"Handling message: {message_type}")

        if message_type == "user_account_login_ok":
            username = validated.username
            token = validated.token
            self.game_state["token"] = token
            await self.tts.speak(f"Login successful for {username}.")
            self.sound.play_menu_sound("menu_click.wav")
            # Possibly trigger UI changes in a thread-safe manner
            # e.g., asyncio.get_running_loop().call_soon_threadsafe(update_ui_login_success)

        elif message_type == "user_account_create_ok":
            username = validated.username
            role = validated.role
            await self.tts.speak(f"Account created for {username} with role {role}. You can now log in.")
            self.sound.play_menu_sound("menu_click.wav")

        # Add more elif cases for other message types

        else:
            self.logger.debug(f"No handler for {message_type}.")

    def stop(self):
        self.running = False

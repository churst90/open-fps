# third party imports
import asyncio
import pygame
import logging

# Project imports
from screenmanager import ScreenManager
from gamewindow import GameWindow
from clienthandler import ClientHandler
from connection import Network
from speechmanager import SpeechManager

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
logger = logging.getLogger("client")

class Client:
    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.tts = SpeechManager()
        self.logger = logger
        self.screen_manager = ScreenManager()
        self.network = Network(self.logger)
        self.client_handler = ClientHandler(self.screen_manager, self.network, self.tts)
        self.initialize_pygame()

    def initialize_pygame(self):
        pygame.init()
        self.main_window = GameWindow(1200, 800, "Open FPS", self.screen_manager)
        self.screen_manager.add_screen(self.main_window, "main_window")
        self.screen_manager.push_screen("main_window")

    async def start(self):
        running = True
        task = None
        modal_active = False  # Flag to indicate if a modal window is active

        while running:
            for event in pygame.event.get():
                if event.type == pygame.QUIT:
                    running = False

            if not self.client_handler.player["logged_in"] and not modal_active:
                # Show the startup menu
                selected_option = await self.client_handler.main_menu()

                if selected_option == "Create account":
                    if task is not None:
                        task.cancel()
                    task = asyncio.create_task(self.client_handler.create_account(self.host, self.port))
                    modal_active = True

                elif selected_option == "Login":
                    if task is not None:
                        task.cancel()
                    task = asyncio.create_task(self.client_handler.login(self.host, self.port))
                    modal_active = True

                elif selected_option == "Exit":
                    running = False

            if modal_active and task:
                try:
                    await asyncio.wait_for(task, timeout=0.01)
                except asyncio.TimeoutError:
                    pass  # The task is still running
                except asyncio.CancelledError:
                    modal_active = False  # Reset the flag when the task is cancelled

            self.screen_manager.update()
            self.main_window.update()

        if task and not task.done():
            task.cancel()
        pygame.quit()

if __name__ == '__main__':
    client = Client("localhost", 33288)
    loop = asyncio.get_event_loop()  # Create a new event loop

    try:
        loop.run_until_complete(client.start())  # Run the client start coroutine
    except Exception as e:
        logging.error(f"An error occurred: {e}")
    finally:
        loop.close()  # Close the loop only when everything is done
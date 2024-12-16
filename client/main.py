# main.py
import asyncio
import logging
import tkinter as tk

from utils.settings_manager import SettingsManager
from utils.logger import setup_logger
from ui.main_menu import MainMenu
from speech.tts_manager import TTSManager
from audio.audio_service import AudioService
from ui.window_manager import WindowManager

async def main_async():
    setup_logger()
    logger = logging.getLogger("MainAsync")

    # Load settings
    settings = SettingsManager.load_settings("config/settings.json") or {}
    speech_cfg = settings.get("speech", {"rate": 300, "volume": 1.0, "voice": ""})

    # Initialize TTS (using accessible_output2 now)
    tts = TTSManager(speech_cfg)
    await tts.start()

    # Initialize AudioService
    audio_service = AudioService(settings)

    # Setup Tkinter
    root = tk.Tk()
    root.title("Open Audio Game Client")

    # Initialize WindowManager
    WindowManager.initialize(root)

    # Create the main menu
    # Note: Now we pass audio_service directly, not just sound_manager
    main_menu = MainMenu(root, tts_manager=tts, audio_service=audio_service, settings=settings)
    main_menu.pack(fill="both", expand=True)

    tts.speak("Client starting. Main menu loaded.")

    # Non-blocking Tkinter loop integration
    async def tk_loop():
        while True:
            if not root.winfo_exists():
                break
            root.update()
            await asyncio.sleep(0.01)

    tk_task = asyncio.create_task(tk_loop())

    def on_close():
        tts.speak("Exiting client.")
        audio_service.cleanup()
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", on_close)

    # Wait until the root window is closed
    while True:
        if not root.winfo_exists():
            break
        await asyncio.sleep(0.1)

    # Cleanup
    await tts.stop()
    logger.info("Client has fully exited.")
    tk_task.cancel()

def main():
    asyncio.run(main_async())

if __name__ == "__main__":
    main()

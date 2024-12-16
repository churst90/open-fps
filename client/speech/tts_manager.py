# speech/tts_manager.py
import logging
import asyncio
import os
from accessible_output2.outputs.auto import Auto

class TTSManager:
    """
    TTSManager using accessible_output2 exclusively.
    Removed NVDA and pyttsx3 references and no call to auto.stop().
    interrupt_speech() just clears the queue.
    """

    def __init__(self, config):
        self.logger = logging.getLogger("TTSManager")

        self.rate = config.get("rate", 200)  # accessible_output2 may ignore these
        self.volume = config.get("volume", 1.0)
        self.voice = config.get("voice", "")

        self.auto = Auto()

        self.queue = asyncio.Queue()
        self.running = True
        self.task = None

    async def start(self):
        self.logger.debug("Starting TTS background task.")
        self.task = asyncio.create_task(self._run())

    async def stop(self):
        self.running = False
        await self.queue.put(None)
        if self.task:
            await self.task
        self.logger.debug("TTS background task stopped.")

    def speak(self, text: str):
        if not text.strip():
            return
        loop = asyncio.get_event_loop()
        loop.call_soon_threadsafe(self.queue.put_nowait, text)

    async def _run(self):
        self.logger.debug("TTS run loop started.")
        while self.running:
            utterance = await self.queue.get()
            if utterance is None:
                break
            await self._speak_async(utterance)
        self.logger.debug("TTS run loop ended.")

    async def _speak_async(self, text: str):
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._speak_blocking, text)

    def _speak_blocking(self, text:str):
        self.logger.debug(f"Speaking text with accessible_output2: {text}")
        self.auto.speak(text)

    def interrupt_speech(self):
        """
        Clear the queue so no further utterances occur.
        accessible_output2 doesn't support immediate stop mid-utterance.
        """
        self.logger.debug("Interrupting speech by clearing queue.")
        while not self.queue.empty():
            try:
                self.queue.get_nowait()
                self.queue.task_done()
            except:
                break

    def reinit_tts(self):
        # If needed, reload settings and adjust rate/voice if accessible_output2 supports it.
        # Otherwise, no-op.
        self.logger.info("TTS reinitialized (no effect if accessible_output2 doesn't support rate/voice changes).")

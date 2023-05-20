from accessible_output2 import outputs

class SpeechManager:
  def __init__(self):
    pass

  def speak(self, text):
    if not isinstance(text, str):
      text = f"{text}"

    tts = outputs.auto.Auto()
    tts.speak(text)
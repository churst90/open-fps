import openal
import numpy as np

class SoundManager:
    def __init__(self):
        self.device = openal.oalGetDevice()
        self.context = openal.oalCreateContext(self.device)
        self.listener_position = np.array([0.0, 0.0, 0.0])
        self.listener_orientation = np.array([0.0, 0.0, -1.0, 0.0])  # Forward direction

    def initialize(self):
        openal.oalSetCurrentContext(self.context)
        openal.oalListenerfv(openal.AL_POSITION, self.listener_position)
        openal.oalListenerfv(openal.AL_ORIENTATION, self.listener_orientation)

    def load_sound(self, sound_file):
        return openal.oalOpen(sound_file)

    def play_sound(self, sound_source):
        source = openal.oalGenSources(1)
        source.set_position(self.listener_position)
        source.set_looping(False)
        source.set_source(sound_source)
        source.play()

    def update_listener_position(self, position):
        self.listener_position = np.array(position)
        openal.oalListenerfv(openal.AL_POSITION, self.listener_position)

    def update_listener_orientation(self, orientation):
        self.listener_orientation = np.array(orientation)
        openal.oalListenerfv(openal.AL_ORIENTATION, self.listener_orientation)

    def cleanup(self):
        openal.oalDestroyContext(self.context)
        openal.oalCloseDevice(self.device)

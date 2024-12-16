# ui/window_manager.py
import logging

class WindowManager:
    """
    A centralized manager for windows and dialogs.
    Keeps track of the main window (root) and any open dialogs.
    Ensures that when a dialog closes, focus returns to the previously focused window.
    Can also announce window changes via TTS if integrated.

    Usage:
    - Call WindowManager.initialize(root) after creating the main window.
    - When opening a dialog: WindowManager.register_dialog(dialog_instance)
    - When closing a dialog: WindowManager.unregister_dialog(dialog_instance)
    """

    _instance = None

    def __init__(self):
        self.logger = logging.getLogger("WindowManager")
        self.root = None
        self.dialog_stack = []  # stack of currently open dialogs (topmost last)

    @classmethod
    def initialize(cls, root):
        if cls._instance is None:
            cls._instance = cls()
            cls._instance.root = root
        return cls._instance

    @classmethod
    def get_instance(cls):
        return cls._instance

    def register_dialog(self, dialog):
        """
        Register a new dialog window. Push it onto the stack.
        """
        self.dialog_stack.append(dialog)
        self.logger.debug(f"Dialog {dialog} registered. Stack size: {len(self.dialog_stack)}")

    def unregister_dialog(self, dialog):
        """
        Unregister a dialog when it closes. If it's the topmost dialog,
        remove it and return focus to the previous window or the main root.
        """
        if dialog in self.dialog_stack:
            self.dialog_stack.remove(dialog)
            self.logger.debug(f"Dialog {dialog} unregistered. Stack size: {len(self.dialog_stack)}")

        # If no dialogs open, return focus to root (if it still exists)
        if not self.dialog_stack and self.root and self.root.winfo_exists():
            self.logger.debug("No dialogs open. Returning focus to main window.")
            self.root.focus_set()

    def get_top_dialog(self):
        """
        Return the topmost dialog if any.
        """
        if self.dialog_stack:
            return self.dialog_stack[-1]
        return None

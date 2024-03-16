# server_constants.py

# Server Metadata
VERSION = "version 1.0 Alpha"
DEVELOPER_NAME = "Cody Hurst"
SERVER_NAME = "Open FPS Game Server"
WEBSITE_URL = "https://codyhurst.com/"

# Server Configuration
DEFAULT_HOST = "localhost"
DEFAULT_PORT = 33288
CERT_FILE = "cert.pem"
KEY_FILE = "key.pem"

# Security Configuration
SECURITY_KEY_FILE = "security.key"
CERT_COMMON_NAME = "/CN=localhost"
CERT_KEY_TYPE = "rsa:4096"
CERT_VALIDITY_DAYS = 365

# System Messages
MOTD = "Welcome to Open FPS Game Server!"
STARTUP_MESSAGE = f"{SERVER_NAME} {VERSION}\nDeveloped and maintained by {DEVELOPER_NAME}. {WEBSITE_URL}"
SHUTDOWN_MESSAGE = "Server shutdown complete."

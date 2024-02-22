# server.py
import argparse
import subprocess
import os
import asyncio
from core.custom_logger import CustomLogger
from core.server_console import ServerConsole
from core.network import Network
from core.server_handler import ServerHandler
from core.security.security_manager import SecurityManager
from core.events.event_dispatcher import EventDispatcher
from core.modules.map_manager import MapRegistry
from core.modules.user_manager import UserRegistry
from core.data import Data

class Server:
    def __init__(self, host, port):
        self.version = "version 1.0 Alpha"
        self.dev_name = "Cody Hurst"
        self.server_name = "Open FPS Game Server"
        self.website = "https://codyhurst.com/"
        self.host = host
        self.port = port
        self.logger = CustomLogger('server', debug_mode = False)
        self.network = None
        self.user_reg = UserRegistry()
        self.map_reg = MapRegistry()
        self.event_dispatcher = EventDispatcher.get_instance(self.network)
        self.server_handler = ServerHandler(self.user_reg, self.map_reg, self.event_dispatcher, self.logger)
        self.console = None
        self.shutdown_event = asyncio.Event()
        self.security_manager = SecurityManager('security.key')
        self.data = None

    def ensure_ssl_certificate(self, cert_file='cert.pem', key_file='key.pem'):
        if not os.path.exists(cert_file) or not os.path.exists(key_file):
            print("Neither a certificate nor a key were found. Generating a self signed certificate now .....")
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:4096', '-keyout', key_file, '-out', cert_file, '-days', '365', '-nodes', '-subj', '/CN=localhost'], check=True)
            print("Certificate and key pair generated successfully")

    async def process_message_queue(self):
        while not self.shutdown_event.is_set():
            data = await self.network.message_queue.get()
            await self.process_message(data)

    async def process_message(self, data):
        message_type = data.get('message_type')
        handler = getattr(self.server_handler, message_type, message_type)
        if handler:
            await handler(data)
        else:
            self.logger.info(f"Unknown message type: {message_type}")

    async def setup_security(self):
        print("Setting up the security manager ...")
        self.security_manager.get_instance("security.key")
        await self.security_manager.load_key()
        await self.security_manager.start_key_rotation(30)

    async def start(self):
        await asyncio.to_thread(self.ensure_ssl_certificate)
        print("Initializing server ...")
        print(f"{self.server_name} {self.version}")
        print(f"Developed and maintained by {self.dev_name}. {self.website}")
        print("Type 'help' for a list of available commands")
        await self.setup_security()
        await self.map_reg.load_maps()
        await self.user_reg.load_users()
        self.network = Network.get_instance(self.host, self.port, asyncio.Queue(), self.process_message, self.shutdown_event)
        await self.network.start()
        asyncio.create_task(self.process_message_queue())
        self.console = ServerConsole.get_instance(self, self.user_reg, self.map_reg, self.logger, self.shutdown_event)
        self.console.start()

    async def shutdown(self):
        print("Shutting down the server...")
        # Stop key rotation first
        await self.security_manager.stop_key_rotation()
        await self.network.stop()
        if self.console:
            await self.console.stop()
        print("Server shutdown complete.")
        self.shutdown_event.set()

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default="localhost")
    parser.add_argument("--port", type=int, default=33288)
    args = parser.parse_args()
    server = Server(args.host, args.port)
    
    await server.start()  # Start the server
    
    # Wait for the shutdown event to be set before proceeding to shutdown
    await server.shutdown_event.wait()
    
if __name__ == '__main__':
    asyncio.run(main())

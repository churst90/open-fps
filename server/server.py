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

class Server:
    def __init__(self, host, port):
        self.version = "version X.Y"
        self.dev_name = "Developer Name"
        self.server_name = "My FPS Game Server"
        self.website = "https://Mywebsite.com/"
        self.host = host
        self.port = port
        self.logger = CustomLogger('server', debug_mode=True)
        self.network = Network(host, port, asyncio.Queue(), self.process_message)
        self.user_reg = UserRegistry()
        self.map_reg = MapRegistry()
        self.event_dispatcher = EventDispatcher.get_instance()
        self.server_handler = ServerHandler(self.user_reg, self.map_reg, self.network, self.event_dispatcher, self.logger)
        self.console = ServerConsole(self.user_reg, self.map_reg, self.logger)
        self.listen_task = None
        self.user_input_task = None
        self.shutdown_event = asyncio.Event()
        self.security_manager = SecurityManager.get_instance("security.key")

    async def ensure_ssl_certificate(self, cert_file='cert.pem', key_file='key.pem'):
        if not os.path.exists(cert_file) or not os.path.exists(key_file):
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:4096', '-keyout', key_file, '-out', cert_file, '-days', '365', '-nodes', '-subj', '/CN=localhost'], check=True)

    async def process_message(self, data, client_socket):
        message_type = data.get('type')
        handler = getattr(self.server_handler, message_type, None)
        if handler:
            await handler(data, client_socket)
        else:
            self.logger.info(f"Unknown message type: {message_type}")

    async def setup_security(self):
        self.logger.info("Setting up the security manager ...")
        await self.security_manager.load_key()
        self.security_manager.start_key_rotation(30)

    async def initialize(self):
        self.logger.info(f"{self.server_name} {self.version}")
        self.logger.info(f"Developed and maintained by {self.dev_name}. {self.website}")
        self.logger.info("All suggestions and comments are welcome")
        await self.setup_security()
        self.logger.info("Loading maps ...")
        await self.map_reg.load_maps()
        self.logger.info("Loading users ...")
        await self.user_reg.load_users()

    async def start(self):
        await self.ensure_ssl_certificate()
        await self.initialize()
        self.listen_task = asyncio.create_task(self.network.accept_connections())
        self.user_input_task = asyncio.create_task(self.console.user_input())
        await asyncio.wait([self.listen_task, self.user_input_task], return_when=asyncio.FIRST_COMPLETED)

    async def shutdown(self):
        self.logger.info("Shutting down the server...")
        tasks = [self.listen_task, self.user_input_task]
        for task in tasks:
            if task and not task.done():
                task.cancel()
                await task
        await self.security_manager.stop_key_rotation()
        await self.network.close()
        self.shutdown_event.set()

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default="localhost")
    parser.add_argument("--port", type=int, default=33288)
    args = parser.parse_args()
    server = Server(args.host, args.port)
    try:
        await server.start()
    except KeyboardInterrupt:
        await server.shutdown()

if __name__ == '__main__':
    asyncio.run(main())

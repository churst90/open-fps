# Game Server Documentation

Welcome to the Game Server project! This server is designed to power a multiplayer game, supporting dynamic map creation, user management, and event-driven architecture. The server integrates features like security management, real-time map updates, and accessibility for various game elements.

## Table of Contents

- [Features](#features)
- [Setup](#setup)
  - [Requirements](#requirements)
  - [Installation](#installation)
  - [Running the Server](#running-the-server)
- [Architecture Overview](#architecture-overview)
  - [Core Components](#core-components)
  - [Event-Driven Design](#event-driven-design)
- [Key Functionalities](#key-functionalities)
  - [Map Management](#map-management)
  - [Tile and Zone Management](#tile-and-zone-management)
  - [User Management](#user-management)
  - [Security](#security)
- [Default Configuration](#default-configuration)
- [Development Guide](#development-guide)
- [Contributing](#contributing)
- [License](#license)

---

## Features

- **Dynamic Map Management**:
  - Create, load, update, and save maps.
  - Support for tiles and zones as dictionaries for efficient access and manipulation.

- **User Management**:
  - Create, delete, and authenticate users with different roles (e.g., admin, player).

- **Event-Driven Architecture**:
  - Modular, scalable design for handling various game events like tile additions, map updates, and user actions.

- **Secure Communication**:
  - Implements SSL encryption and key rotation for secure networking.

- **Customizable Default Assets**:
  - Preload default maps, tiles, and zones during server initialization.

- **Real-Time Networking**:
  - Supports real-time event dispatching and message processing.

---

## Setup

### Requirements

- Python 3.9+
- Required libraries (can be installed via `requirements.txt`):
  - `aiofiles`
  - `asyncio`
  - `uuid`
  - Custom modules (e.g., `include.custom_logger`, `include.event_dispatcher`)

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/your-repo/game-server.git
   cd game-server
   ```

2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

3. Set up the directory structure:
   - Ensure the following directories exist:
     - `maps/`: For storing map files.
     - `users/`: For storing user data.

4. Configure the server:
   - Edit `include/server_constants.py` to adjust server configurations like default host, port, and other constants.

---

### Running the Server

To start the server:

```bash
python server.py --host <your-host> --port <your-port>
```

Example:
```bash
python server.py --host 127.0.0.1 --port 5000
```

The server logs will indicate its status, including initialized components, loaded assets, and any errors.

---

## Architecture Overview

### Core Components

1. **Server**:
   - Entry point for the application.
   - Handles initialization, setup, and shutdown.

2. **MapRegistry**:
   - Manages map loading, saving, and modification.
   - Supports efficient tile and zone management using dictionaries.

3. **MapService**:
   - Processes map-related events such as creation, tile addition/removal, and user interactions with maps.

4. **UserRegistry**:
   - Manages user authentication and account information.

5. **EventDispatcher**:
   - Implements an event-driven model for handling game actions in real-time.

6. **Network**:
   - Handles client-server communication with message queues and event dispatching.

7. **SecurityManager**:
   - Ensures secure communication using SSL certificates and key rotation.

### Event-Driven Design

The server leverages an event-driven model where components subscribe to specific events. For example:
- A `map_create_request` triggers the creation of a new map in `MapRegistry`.
- A `map_tile_add_request` updates a map's tiles dynamically.

This design ensures flexibility and scalability for new features.

---

## Key Functionalities

### Map Management

- **Create a Map**:
  - Use the `map_create_request` event to create a new map with customizable properties like size, start position, and tiles.

- **Load and Save Maps**:
  - Maps are stored in a custom format and can be serialized/deserialized using the `MapParser`.

- **Default Map Setup**:
  - During initialization, a default map (`Main`) is created with predefined tiles and zones.

### Tile and Zone Management

- **Tiles**:
  - Represent areas of the map with properties like position, type, and wall status.
  - Stored as a dictionary for fast access and updates.

- **Zones**:
  - Define areas of interest within maps (e.g., safe zones, hazards).
  - Managed similarly to tiles, using dictionaries.

### User Management

- **Account Creation**:
  - Default admin account is created during the first run if no user data exists.
  - Additional users can be added via `user_account_create_request`.

- **User Roles**:
  - Support for different roles, such as admin, player, and developer.

### Security

- **SSL Encryption**:
  - All communication is encrypted using SSL certificates.

- **Key Rotation**:
  - Automatically rotates keys periodically to enhance security.

---

## Default Configuration

During the first run:
- A default map (`Main`) is created with the following properties:
  - Size: `(0, 10, 0, 10, 0, 10)`
  - Start Position: `(0, 0, 0)`
  - Owners: `admin`
  - Tiles:
    ```python
    {
        "uuid1": {"tile_position": (0, 0, 0, 1, 1, 1), "tile_type": "grass", "is_wall": False},
        "uuid2": {"tile_position": (1, 1, 0, 2, 2, 1), "tile_type": "dirt", "is_wall": False},
        "uuid3": {"tile_position": (2, 2, 0, 3, 3, 1), "tile_type": "brick", "is_wall": True}
    }
    ```

- Default admin credentials:
  - Username: `admin`
  - Password: `adminpass`

---

## Development Guide

### Adding New Events

1. Define a new event type (e.g., `map_update_request`).
2. Subscribe to the event in the appropriate service (e.g., `MapService`):
   ```python
   self.event_dispatcher.subscribe('map_update_request', self.update_map)
   ```
3. Implement the event handler:
   ```python
   async def update_map(self, event_data):
       # Handle the map update logic here
       pass
   ```

### Adding New Features

- Follow the modular architecture:
  - Use registries for resource management (e.g., maps, users).
  - Use services for event handling and business logic.
  - Use the event dispatcher for communication between components.

---

## Contributing

Contributions are welcome! To contribute:
1. Fork the repository.
2. Create a new branch for your feature or bug fix.
3. Submit a pull request with a clear description of your changes.

---

## License

This project is licensed under the [MIT License](LICENSE).

---

Feel free to extend this `README.md` as you add more features to the game server!
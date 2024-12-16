# Open Audio Game Server and Client

## Overview
This project is a work-in-progress server and corresponding client for a fully-audio-based game. The server aims to provide a robust backend for:
- User authentication and session management (via JWT)
- Role-based permissions and access control
- Map management (maps, tiles, zones, physics)
- Movement and interaction within a 3D environment represented entirely by data and audio cues
- Communication channels (chat systems: private, map-level, global, and server announcements)
- Integration with AI entities
- Physics systems for entities and maps
- Security enhancements (SSL/TLS via a self-signed certificate if none provided, token-based auth, and role-based authorization)

The code is structured into multiple directories and services, each responsible for a particular domain of the server.

## Current Capabilities

### Server-Side
- **User Management**:  
  - Create user accounts (`user_account_create_request`)
  - Login/Logout with JWT-based authentication (`user_account_login_request`, `user_account_logout_request`)
  - Roles and Permissions: "developer", "player", "moderator", etc. to govern who can create maps, spawn AI, etc.

- **Maps, Tiles, Zones**:  
  - Create, remove, and modify maps (`map_create_request`, `map_remove_request`)
  - Add/remove tiles and zones (`map_tile_add_request`, `map_tile_remove_request`, `map_zone_add_request`, `map_zone_remove_request`)
  - Join/leave maps (`map_join_request`, `map_leave_request`)
  - Update map physics settings (`map_physics_update_request`)

- **Movement and Orientation**:  
  - Move within a map (`user_move_request`)
  - Turn orientation (`user_turn_request`)
  - Jump action (`user_jump_request`)

- **Chat and Communication**:  
  - Send private, map-level, global, or server messages (`chat_message`)

- **AI Management**:  
  - Spawn, remove, move, and update AI entities if you have permissions (`ai_spawn_request`, `ai_remove_request`, `ai_move_request`, `ai_update_health_request`)

- **Collision and Physics**:  
  - Collision checks via `collision_manager`
  - Physics can be configured per map, including gravity and friction
  - Tiles, zones, and user positions can use floating-point coordinates to allow finer movement granularity

- **Security**:  
  - SSL/TLS via `SSLManager` and `SecurityManager`
  - Self-signed certificate creation on startup if none found
  - JWT-based authentication and token validation
  - Role-based authorization integrated with `RoleManager`

- **Logging and Persistence**:  
  - Chat logs are stored per category (global, map-specific, server, private)
  - Users, maps, AI, and items are stored on the filesystem by default
  - Potential integration with a database if desired (some repository interfaces are available)

### Client-Side (WIP)
- A reference client skeleton in Tkinter and audio with pyOpenAL and NVDA-based TTS is outlined in the code base.
- The client can:
  - Connect to the server via SSL/TLS if enabled
  - Send JSON messages to the server according to the message schema
  - Login, create accounts, join maps, and move around
  - Issue chat messages and receive updates from the server
  - Eventually reflect map state changes, player movements, sounds, and so forth
- Currently, the client code is incomplete and serves more as a reference. It shows how you'd integrate menu navigation, TTS output, and openal-based audio.

## What Works and What Doesn’t
- **Works**:
  - You can start the server, and it will generate a self-signed certificate if none found.
  - You can create users, log in, and receive JWT tokens.
  - You can create a default map (like "Main") on server startup if none exists.
  - Map joining, leaving, and basic movement requests are handled.
  - Chat messages can be sent to the server, which logs them and would broadcast to relevant clients.
  
- **In Progress**:
  - Client accessibility features (NVDA speech) are partially implemented but may require adjustments.
  - The client’s UI, event handling, and integration with server messages are not fully tested or completed.
  - Enhanced physics and environmental interactions (e.g., more complex physics beyond gravity, sophisticated AI logic) are placeholders or simplified.

- **To Do/Not Implemented**:
  - Advanced item interactions and crafting logic is stubbed but not fully fleshed out.
  - Detailed database integration for persistent storage beyond file-based storage.
  - Additional permissions and role granularity. The `roles.json` and `user_roles.json` can be extended.
  - Better synchronization to ensure race-free state updates if the server gets heavily concurrent.
  - More robust error handling and recovery in both server and client code.

## How to Run the Server
1. Ensure Python 3.9+ installed.
2. `pip install -r requirements.txt` (requirements not fully specified here, but you’d need libraries for async, JWT, etc.)
3. Run `python server.py` or `python main.py` depending on your entry point.
4. On first run, a self-signed certificate is generated if none found.
5. Server starts and listens on `localhost:33288` by default (changeable in `main.py`).
6. Use a client (or a tool like `openssl s_client` or `wscat` if using TLS) to connect and send JSON messages as outlined.

## How to Interact With the Server
- All communication is via JSON lines. Each message:
  ```json
  {
    "client_id": "your_unique_id",
    "message": {
      "message_type": "<some_request_type>",
      ... additional fields ...
    }
  }

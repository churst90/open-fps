# Open FPS Audio Game Server and Client

## Overview

Open FPS is a groundbreaking, audio-based 3D FPS game with a server-client architecture, designed to deliver an immersive auditory gaming experience. It enables real-time multiplayer interactions in a 3D world, accentuated with sound for navigation, combat, and environmental interaction. This project encourages community involvement for developing, customizing, and expanding game environments. Its server handles game logic and player interactions, while the client focuses on processing and presenting gam...

## Key Features

- **Audio-Based Gameplay:** Experience a 3D game environment through audio, enhancing accessibility and immersion.
- **Dynamic Content Creation:** Users can create maps, items, and AI characters to enrich the gameplay experience.
- **Real-Time Multiplayer:** Players can move, fight, and communicate in a shared game space.
- **Secure Authentication:** Features secure login mechanisms and encrypted data storage for player information.
- **Client-Side Sound Processing:** Offers advanced sound processing for realistic spatial audio effects.

## Development Status

### Server

#### Completed

- Basic infrastructure including networking, player management, and initial gameplay functionalities.
- A chat system for communication between players.

#### In Progress

- Refining AI interactions and behaviors.
- Improving map and state management for players.

#### Planned

- Extending map creation tools.
- Voice chat for enhanced player interaction.

### Client

#### Completed

- Initial setup for connecting to the server and basic user interaction.

#### In Progress

- Implementing spatial sound processing for an immersive audio experience.
- Developing a user-friendly interface for navigation and interaction.

#### Issues

- Dialog windows for login and account creation need refinement for better usability.
- Integration of the client with the server for real-time updates and interactions is ongoing.

### General

#### Planned

- Advanced combat mechanics.
- Comprehensive community documentation for contributing to game development.

## Current Challenges and Roadmap

- **Serialization for Maps and Users:** Transitioned from using pickle to JSON for enhanced security and interoperability. The current challenge is ensuring complex objects like maps and players are correctly serialized/deserialized.
- **Client Development:** Focus on enhancing the user interface and interaction model, especially improving dialog windows and keyboard input handling.
- **Spatial Audio Processing:** Aiming to implement more sophisticated sound processing techniques for a realistic audio environment.

## Getting Started

### Prerequisites

- Python 3.8 or higher.
- Necessary libraries: `asyncio`, `cryptography`, `logging`, `json`, and client-specific audio processing libraries.

### Installation

1. Clone the repository.
2. Install the required dependencies using `pip`.

### Running the Application

- **Server:** `python server.py --host <your_host> --port <your_port>`.
- **Client:** Instructions for starting the client will depend on ongoing development and will be updated as the client matures.

## Contributing

Open FPS encourages open-source contributions. Whether it's improving the server, enhancing the client's audio processing, or creating custom content, all contributions are welcome.

- **Fork and Pull:** Fork the repository, make your changes, and submit a pull request.
- **Issues:** Use the GitHub issue tracker to report bugs or suggest features.

## License

This project is under the MIT License. See [LICENSE.md](LICENSE.md) for more details.
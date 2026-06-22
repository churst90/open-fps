using System.Windows.Forms;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core.Input;

public class CombatProcessor
{
    private readonly ClientNetworkService _network;
    private readonly ClientWorldState _world;
    private readonly LocalPlayerState _state;

    public CombatProcessor(ClientNetworkService network, ClientWorldState world, LocalPlayerState state)
    {
        _network = network;
        _world = world;
        _state = state;
    }

    public void RegisterBindings(InputCommandMapper mapper)
    {
        mapper.Bind(InputContext.Gameplay, Keys.E, HandleInteract);
        mapper.Bind(InputContext.Gameplay, Keys.Enter, HandleInteract);
        mapper.Bind(InputContext.Gameplay, Keys.P, () => _network.Send(new TextCommand { Command = "scan" }));
        mapper.Bind(InputContext.Gameplay, Keys.I, () => _network.Send(new TextCommand { Command = "inv" }));
    }

    private void HandleInteract()
    {
        var targetId = _world.GetClosestEntityId(_state.Position);
        if (targetId.HasValue) 
        {
            _network.Send(new InteractRequest { Action = "interact", TargetEntityId = targetId.Value });
        }
    }
}

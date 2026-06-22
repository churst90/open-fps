using System.Windows.Forms;
using OpenFPS.Client.Services;
using System.Numerics;

namespace OpenFPS.Client.Core.Input;

public class AccessibilityProcessor
{
    private readonly TolkService _tts;
    private readonly LocalPlayerState _state;
    private readonly ClientWorldState _world;

    public AccessibilityProcessor(TolkService tts, LocalPlayerState state, ClientWorldState world)
    {
        _tts = tts;
        _state = state;
        _world = world;
    }

    public void RegisterBindings(InputCommandMapper mapper)
    {
        mapper.Bind(InputContext.Gameplay, Keys.C, () => _tts.Speak($"Coordinates: {_state.Position.X:F1}, {_state.Position.Y:F1}, {_state.Position.Z:F1}"));
        mapper.Bind(InputContext.Gameplay, Keys.Z, () => _tts.Speak($"Area: {_state.CurrentRegion}"));
        mapper.Bind(InputContext.Gameplay, Keys.H, () => _tts.Speak($"Health: {_state.Health}%"));
        mapper.Bind(InputContext.Gameplay, Keys.F, () => _tts.Speak($"Facing: {_state.GetCompassDirection()}"));
        mapper.Bind(InputContext.Gameplay, Keys.Oemcomma, HandleLookAhead);
    }

    private void HandleLookAhead()
    {
        var spatial = new SpatialService();
        var snapshot = _world.GetSnapshot();
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), _state.Rotation);
        Vector3 eyePos = _state.Position + new Vector3(0, 1.7f, 0);

        if (spatial.RaycastSingle(snapshot, eyePos, forward, 20.0f, out var hitEntity, out float dist))
        {
            string name = hitEntity.Definition.Identity.Name;
            if (string.IsNullOrEmpty(name)) name = "Unknown Object";
            _tts.Speak($"{name}, {dist:F1} meters ahead.");
        }
        else
        {
            _tts.Speak("Nothing directly ahead.");
        }
    }
}

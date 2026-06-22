using System.Windows.Forms;
using OpenFPS.Client.Services;
using OpenFPS.Common.Networking;
using System;

namespace OpenFPS.Client.Core.Input;

public class SystemInputProcessor
{
    private readonly ClientNetworkService _network;
    private readonly ChatManager _chat;
    private readonly Action _openCommandWindow;
    private readonly Action _confirmQuit;

    public SystemInputProcessor(ClientNetworkService network, ChatManager chat, Action openCommandWindow, Action confirmQuit)
    {
        _network = network;
        _chat = chat;
        _openCommandWindow = openCommandWindow;
        _confirmQuit = confirmQuit;
    }

    public void RegisterBindings(InputCommandMapper mapper)
    {
        // Social / Discovery
        mapper.Bind(InputContext.Global, Keys.F5, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Server }));
        mapper.Bind(InputContext.Global, Keys.F6, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Map }));
        mapper.Bind(InputContext.Global, Keys.F7, () => _network.Send(new FriendListRequest()));

        // Chat
        mapper.Bind(InputContext.Global, Keys.OemOpenBrackets, () => {
            if (Control.ModifierKeys.HasFlag(Keys.Shift)) _chat.CycleBuffer(-1);
            else _chat.CycleMessage(-1);
        }); 
        mapper.Bind(InputContext.Global, Keys.OemCloseBrackets, () => {
            if (Control.ModifierKeys.HasFlag(Keys.Shift)) _chat.CycleBuffer(1);
            else _chat.CycleMessage(1);
        });

        // UI
        mapper.Bind(InputContext.Global, Keys.OemQuestion, _openCommandWindow);
        mapper.Bind(InputContext.Global, Keys.Divide, _openCommandWindow);
        mapper.Bind(InputContext.Global, Keys.Escape, _confirmQuit);
    }
}

using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Xande.TestPlugin;

public class Service {
    [PluginService]
    public static DalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    public static IFramework Framework { get; private set; } = null!;

    [PluginService]
    public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    public static IPluginLog Logger { get; private set; } = null!;

    [PluginService]
    public static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    public static IDataManager GameData { get; private set; } = null!;
    [PluginService]
    public static IObjectTable ObjectTable { get; private set; } = null!;
}
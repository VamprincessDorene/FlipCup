using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FlipCup.Sync;

namespace FlipCup;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/flipcup";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    public Configuration Config { get; }
    public Game Game { get; }
    public CupOverlay Overlay { get; }
    public FurnitureCups Furniture { get; }
    public OnlineSession Session { get; }

    private readonly WindowSystem windows = new("FlipCup");
    private readonly MainWindow mainWindow;

    // Guest: the host's table layout, kept so we can (re)bind the cups once we're in the house.
    private LayoutDto? hostLayout;
    private bool hostLayoutApplied;
    private float layoutRetry;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.PlayerNames is not { Length: 8 }) Config.PlayerNames = new string[8];
        if (Config.TeamNames is not { Length: 2 }) Config.TeamNames = new[] { "Team Red", "Team Blue" };

        Game = new Game(Config, msg => ChatGui.Print(msg, "Flip Cup"));
        Furniture = new FurnitureCups(Config, DataManager);
        Session = new OnlineSession(Config, Game, msg => ChatGui.Print(msg, "Flip Cup"))
        {
            GetLayout = BuildLayout,
            ApplyLayout = layout => { hostLayout = layout; hostLayoutApplied = false; layoutRetry = 0; },
        };
        Overlay = new CupOverlay(Config, Game, GameGui, Furniture, Session);
        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Flip Cup. '/flipcup host' starts an online game, '/flipcup join CODE' joins one, " +
                          "'/flipcup leave' goes offline, '/flipcup reset' starts a new round.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Session.Dispose();   // host: removes the game from the relay; guest: frees your seat
        // Put the real cups back where the game thinks they are.
        Framework.RunOnFrameworkThread(() => Furniture.Dispose()).Wait();
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(Command);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var dt = (float)framework.UpdateDelta.TotalSeconds;
        lock (Game)
        {
            Session.Pump(dt);
            TryApplyHostLayout(dt);
            Game.Update(dt);
            Furniture.Apply(Game);
        }
    }

    private void OnTerritoryChanged(uint territory)
    {
        Furniture.Forget();
        hostLayoutApplied = false;   // guest: rebind the host's cups once we arrive
    }

    // ----- online play ---------------------------------------------------------------

    private string CharacterName() => ObjectTable.LocalPlayer?.Name.TextValue ?? "Player";

    public void HostOnline()
    {
        Session.MyName = CharacterName();
        _ = Session.HostAsync().ContinueWith(_ =>
            ChatGui.Print(Session.Error.Length > 0 ? Session.Error
                : $"Hosting! Tell your friends to type: /flipcup join {Session.Code}", "Flip Cup"));
    }

    public void JoinOnline(string code)
    {
        Session.MyName = CharacterName();
        hostLayout = null;
        _ = Session.JoinAsync(code).ContinueWith(_ =>
            ChatGui.Print(Session.Error.Length > 0 ? Session.Error
                : $"Joined game {Session.Code}. Open /flipcup and pick a seat.", "Flip Cup"));
    }

    public void LeaveOnline()
    {
        _ = Session.LeaveAsync().ContinueWith(_ => ChatGui.Print("Left the online game.", "Flip Cup"));
        hostLayout = null;
    }

    /// <summary>Host: the table layout guests need to line up with ours.</summary>
    private LayoutDto BuildLayout()
    {
        var l = new LayoutDto
        {
            AnchorX = Config.AnchorX, AnchorY = Config.AnchorY, AnchorZ = Config.AnchorZ,
            AnchorRotation = Config.AnchorRotation, HasAnchor = Config.HasAnchor,
            HeightOffset = Config.HeightOffset, StartDistance = Config.StartDistance,
            Spacing = Config.Spacing, SideOffset = Config.SideOffset,
            CupHeight = Config.CupHeight, ModelHeight = Config.ModelHeight,
        };
        if (Furniture.IsBound)
        {
            l.FurnitureBaseId = Furniture.BoundBaseId;
            l.FurnitureName = Config.FurnitureName;
            l.CupPositions = new float[24];
            for (var i = 0; i < 8; i++)
            {
                var p = Furniture.RestPosition(i);
                l.CupPositions[i * 3] = p.X; l.CupPositions[i * 3 + 1] = p.Y; l.CupPositions[i * 3 + 2] = p.Z;
            }
        }
        return l;
    }

    /// <summary>Guest: use the host's layout. Retries every 2s until the cups are found.</summary>
    private void TryApplyHostLayout(float dt)
    {
        if (Session.Role != Role.Guest || hostLayout == null || hostLayoutApplied) return;
        layoutRetry -= dt;
        if (layoutRetry > 0) return;
        layoutRetry = 2f;

        var l = hostLayout;
        Config.AnchorX = l.AnchorX; Config.AnchorY = l.AnchorY; Config.AnchorZ = l.AnchorZ;
        Config.AnchorRotation = l.AnchorRotation; Config.HasAnchor = l.HasAnchor;
        Config.HeightOffset = l.HeightOffset; Config.StartDistance = l.StartDistance;
        Config.Spacing = l.Spacing; Config.SideOffset = l.SideOffset;
        Config.CupHeight = l.CupHeight; Config.ModelHeight = l.ModelHeight;
        Overlay.Visible = true;

        if (l.FurnitureBaseId == 0 || l.CupPositions is not { Length: 24 })
        {
            if (Furniture.IsBound) Furniture.Unbind();   // host uses drawn cups
            hostLayoutApplied = true;
            return;
        }

        var seats = new System.Numerics.Vector3[8];
        for (var i = 0; i < 8; i++)
            seats[i] = new(l.CupPositions[i * 3], l.CupPositions[i * 3 + 1], l.CupPositions[i * 3 + 2]);
        Config.FurnitureName = l.FurnitureName;
        Config.FurnitureBaseId = l.FurnitureBaseId;
        var error = Furniture.BindToPositions(l.FurnitureBaseId, seats);
        BindStatus = error ?? $"Using the host's 8 \"{l.FurnitureName}\".";
        hostLayoutApplied = error == null;
    }

    /// <summary>Find the placed cup furnishings and assign them to seats (runs on the game thread).</summary>
    public void BindFurniture()
    {
        BindStatus = "Looking for cups...";
        Framework.RunOnFrameworkThread(() =>
        {
            lock (Game)
            {
                var player = ObjectTable.LocalPlayer;
                var error = player == null ? "Can't find your character." : Furniture.Bind(player.Position);
                Game.Reset();
                if (error == null)
                {
                    Overlay.Visible = true;
                    SaveConfig();
                }
                BindStatus = error ?? $"Using 8 \"{Config.FurnitureName}\" as cups.";
                ChatGui.Print(BindStatus, "Flip Cup");
            }
        });
    }

    public string BindStatus { get; private set; } = "";

    /// <summary>Saves what the plugin can see about nearby furniture to pluginConfigs\FlipCup\debug.txt.</summary>
    public void WriteDebugDump()
    {
        Framework.RunOnFrameworkThread(() =>
        {
            var player = ObjectTable.LocalPlayer;
            var text = Furniture.DebugDump(player?.Position ?? default);
            var path = System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "debug.txt");
            PluginInterface.ConfigDirectory.Create();
            System.IO.File.WriteAllText(path, text);
            ChatGui.Print($"Debug info saved to {path}", "Flip Cup");
        });
    }

    public void UnbindFurniture() => Framework.RunOnFrameworkThread(() => { lock (Game) Furniture.Unbind(); });

    private void DrawUi()
    {
        Overlay.Draw();
        windows.Draw();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "here":
                SetAnchorToPlayer();
                break;
            case "debug":
                WriteDebugDump();
                break;
            case "host":
                HostOnline();
                break;
            case "join" when parts.Length > 1:
                JoinOnline(parts[1]);
                break;
            case "join":
                ChatGui.Print("Usage: /flipcup join CODE", "Flip Cup");
                break;
            case "leave":
                LeaveOnline();
                break;
            case "reset":
                NewRound();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    /// <summary>Refill all cups. Online, only the host can do this.</summary>
    public void NewRound()
    {
        if (Session.Role == Role.Guest)
        {
            ChatGui.Print("Only the host can start a new round.", "Flip Cup");
            return;
        }
        lock (Game) Game.Reset();
        ChatGui.Print("New round! Cups are full.", "Flip Cup");
    }

    public void SetAnchorToPlayer()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null) return;
        if (Session.Role == Role.Guest)
        {
            ChatGui.Print("You're in someone else's game; the host sets up the table.", "Flip Cup");
            return;
        }

        Config.AnchorX = player.Position.X;
        Config.AnchorY = player.Position.Y;
        Config.AnchorZ = player.Position.Z;
        Config.AnchorRotation = player.Rotation;
        Config.HasAnchor = true;
        Overlay.Visible = true;
        if (Furniture.IsBound) UnbindFurniture();  // seat assignment depends on where you stand
        SaveConfig();
        Game.Reset();
        ChatGui.Print("Cups placed in front of you. Adjust height/spacing in /flipcup if they're off the counter.", "Flip Cup");
    }

    public void SaveConfig() => PluginInterface.SavePluginConfig(Config);
}

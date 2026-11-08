using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace FlipCup;

/// <summary>
/// Draws rings, names and the winner banner over the real cup furnishings, and catches clicks on them.
/// The game itself draws the cup models (moved by FurnitureCups).
/// </summary>
public sealed class CupOverlay
{
    private readonly Configuration config;
    private readonly Game game;
    private readonly IGameGui gameGui;
    private readonly FurnitureCups furniture;
    private readonly Sync.OnlineSession session;

    public bool Visible = true;

    private static readonly uint[] TeamColor = { Col(200, 40, 40), Col(40, 90, 210) };

    public CupOverlay(Configuration config, Game game, IGameGui gameGui, FurnitureCups furniture, Sync.OnlineSession session)
    {
        this.config = config;
        this.game = game;
        this.gameGui = gameGui;
        this.furniture = furniture;
        this.session = session;
    }

    public Vector3 CupWorldPosition(Cup c) => furniture.RestPosition(c.Team * Game.SeatsPerTeam + c.Seat);

    public void Draw()
    {
        if (!Visible || !furniture.IsBound) return;   // no cups until 8 furnishings are picked

        var dl = ImGui.GetBackgroundDrawList();
        var height = config.ModelHeight;

        foreach (var cup in game.Cups)
        {
            var basePos = CupWorldPosition(cup);
            if (!gameGui.WorldToScreen(basePos, out var baseScr)) continue;
            if (!gameGui.WorldToScreen(basePos + new Vector3(0, height, 0), out var topScr)) continue;

            var h = MathF.Max(8f, Vector2.Distance(baseScr, topScr));
            DrawRing(dl, cup, baseScr - new Vector2(0, h * 0.5f), h);
            DrawLabel(dl, cup, baseScr, h);
            HandleClick(cup, baseScr, h);
        }

        DrawBanner(dl);
    }

    // ----- drawing --------------------------------------------------------

    /// <summary>Pulsing ring around the cup that's up next.</summary>
    private void DrawRing(ImDrawListPtr dl, Cup cup, Vector2 centre, float h)
    {
        if (!game.CanInteract(cup)) return;
        var pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 6f);
        dl.AddCircle(centre, h * 0.85f, WithAlpha(Col(255, 255, 255), (byte)(80 + 120 * pulse)), 32, 2f);
    }

    private void DrawLabel(ImDrawListPtr dl, Cup cup, Vector2 baseScr, float h)
    {
        var mine = session.CanClick(cup);
        string status = cup.State switch
        {
            CupState.Full when mine => "CHUG! (click)",
            CupState.Empty when mine => "FLIP! (click)",
            CupState.Full when game.CanInteract(cup) => "Chugging...",
            CupState.Empty when game.CanInteract(cup) => "Flipping...",
            CupState.OnSide => "Missed!",
            CupState.Down => "Done",
            _ => "",
        };

        var name = game.PlayerName(cup);
        if (session.IsOnline && cup.Owner == session.ClientId) name = "> " + name + " (you) <";
        var nameSize = ImGui.CalcTextSize(name);
        var y = baseScr.Y - h * 2.6f;
        dl.AddText(new Vector2(baseScr.X - nameSize.X / 2, y), Col(255, 255, 255), name);
        if (status.Length > 0)
        {
            var s = ImGui.CalcTextSize(status);
            dl.AddText(new Vector2(baseScr.X - s.X / 2, y + nameSize.Y), Col(255, 230, 120), status);
        }
    }

    private void DrawBanner(ImDrawListPtr dl)
    {
        if (game.Winner < 0) return;
        var text = $"{game.TeamName(game.Winner)} WINS!";
        var vp = ImGui.GetMainViewport();
        var scale = 3f;
        var size = ImGui.CalcTextSize(text) * scale;
        var pos = new Vector2(vp.Pos.X + (vp.Size.X - size.X) / 2, vp.Pos.Y + vp.Size.Y * 0.2f);
        dl.AddRectFilled(pos - new Vector2(20, 12), pos + size + new Vector2(20, 12), Col(0, 0, 0, 170), 10f);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, pos, TeamColor[game.Winner] | 0xFF000000, text);

        if (game.NextRoundIn > 0)
        {
            var sub = $"Next round in {MathF.Ceiling(game.NextRoundIn):F0}...";
            var subSize = ImGui.CalcTextSize(sub);
            dl.AddText(new Vector2(vp.Pos.X + (vp.Size.X - subSize.X) / 2, pos.Y + size.Y + 16), Col(255, 255, 255), sub);
        }
    }

    // ----- input ------------------------------------------------------------

    /// <summary>
    /// A tiny invisible ImGui window over each cup, so clicking a cup is captured by the plugin
    /// instead of passing through to the game (which would move/target instead).
    /// </summary>
    private void HandleClick(Cup cup, Vector2 baseScr, float h)
    {
        if (!session.CanClick(cup)) return;

        var size = new Vector2(h * 1.1f, h * 1.3f);
        ImGui.SetNextWindowPos(baseScr - new Vector2(size.X / 2, size.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                                       ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove |
                                       ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (ImGui.Begin($"##flipcup_cup_{cup.Team}_{cup.Seat}", flags))
        {
            if (ImGui.InvisibleButton("##hit", size))
                session.Click(cup);   // offline/host: clicks locally; guest: asks the host
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    // ----- helpers ----------------------------------------------------------

    private static uint Col(byte r, byte g, byte b, byte a = 255) =>
        (uint)(a << 24 | b << 16 | g << 8 | r);

    private static uint WithAlpha(uint c, byte a) => (c & 0x00FFFFFF) | ((uint)a << 24);
}

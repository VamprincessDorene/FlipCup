using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace FlipCup;

/// <summary>
/// Draws the cups in the game world (projected onto the counter) and catches clicks on them.
/// Layout: you stand at one end of the counter facing along it when you set the anchor.
/// Team 1 is the row on your left, team 2 the row on your right, seat 1 closest to you.
/// </summary>
public sealed class CupOverlay
{
    private readonly Configuration config;
    private readonly Game game;
    private readonly IGameGui gameGui;

    public bool Visible = true;

    private static readonly uint[] TeamColor = { Col(200, 40, 40), Col(40, 90, 210) };
    private static readonly uint RimColor = Col(245, 245, 245);
    private static readonly uint Outline = Col(20, 20, 20, 200);
    private static readonly uint Beer = Col(240, 180, 40, 230);
    private static readonly uint Foam = Col(255, 250, 230);

    private readonly FurnitureCups furniture;
    private readonly Sync.OnlineSession session;

    public CupOverlay(Configuration config, Game game, IGameGui gameGui, FurnitureCups furniture, Sync.OnlineSession session)
    {
        this.config = config;
        this.game = game;
        this.gameGui = gameGui;
        this.furniture = furniture;
        this.session = session;
    }

    public Vector3 CupWorldPosition(Cup c)
    {
        if (furniture.IsBound)
            return furniture.RestPosition(c.Team * Game.SeatsPerTeam + c.Seat);
        return DrawnCupPosition(c);
    }

    private Vector3 DrawnCupPosition(Cup c)
    {
        var r = config.AnchorRotation;
        var forward = new Vector3(MathF.Sin(r), 0, MathF.Cos(r));
        var right = new Vector3(-forward.Z, 0, forward.X);   // perpendicular on the ground plane
        var side = c.Team == 0 ? -1f : 1f;

        var anchor = new Vector3(config.AnchorX, config.AnchorY + config.HeightOffset, config.AnchorZ);
        return anchor
               + forward * (config.StartDistance + c.Seat * config.Spacing)
               + right * (side * config.SideOffset);
    }

    public void Draw()
    {
        if (!Visible || !config.HasAnchor) return;

        var dl = ImGui.GetBackgroundDrawList();
        var real = furniture.IsBound;
        var height = real ? config.ModelHeight : config.CupHeight;

        foreach (var cup in game.Cups)
        {
            var basePos = CupWorldPosition(cup);
            if (!gameGui.WorldToScreen(basePos, out var baseScr)) continue;
            if (!gameGui.WorldToScreen(basePos + new Vector3(0, height, 0), out var topScr)) continue;

            var h = MathF.Max(8f, Vector2.Distance(baseScr, topScr));
            if (real)
                DrawRing(dl, cup, baseScr - new Vector2(0, h * 0.5f), h);  // the game draws the actual cup model
            else
                DrawCup(dl, cup, baseScr, h);
            DrawLabel(dl, cup, baseScr, h);
            HandleClick(cup, baseScr, h);
        }

        DrawBanner(dl);
    }

    // ----- drawing --------------------------------------------------------

    private void DrawCup(ImDrawListPtr dl, Cup cup, Vector2 baseScr, float h)
    {
        var rimW = h * 0.85f;
        var botW = h * 0.58f;

        // Angle 0 = upright (rim up), PI = rim down. Lift = hop height in pixels.
        var (angle, liftH) = cup.Pose();
        var lift = liftH * h;

        var dimmed = !game.CanInteract(cup) && cup.State is CupState.Full or CupState.Empty;
        var bodyCol = dimmed ? WithAlpha(TeamColor[cup.Team], 110) : TeamColor[cup.Team];

        // Rotate around the cup's centre; on its side it rests lower.
        var restDrop = cup.State == CupState.OnSide ? h * 0.5f - rimW * 0.5f : 0;
        var centre = baseScr - new Vector2(0, h * 0.5f + lift - restDrop);

        Vector2 P(float x, float y) => centre + Rotate(new Vector2(x, y), angle);

        // Local coords: +y is down the screen. Rim at -h/2, base at +h/2.
        var rimL = P(-rimW / 2, -h / 2);
        var rimR = P(rimW / 2, -h / 2);
        var botR = P(botW / 2, h / 2);
        var botL = P(-botW / 2, h / 2);

        dl.AddQuadFilled(rimL, rimR, botR, botL, bodyCol);

        // Liquid: fills the lower part of the cup while upright and full / draining.
        var fill = cup.State == CupState.Full ? 1f
                 : cup.ChugTimer > 0 ? cup.ChugTimer / Cup.ChugDuration
                 : 0f;
        if (fill > 0 && angle == 0)
        {
            var top = h / 2 - h * 0.7f * fill;
            var wTop = Lerp(botW, rimW, (h / 2 - top) / h);
            var inset = 0.12f * h;
            dl.AddQuadFilled(P(-wTop / 2 + inset, top), P(wTop / 2 - inset, top),
                             P(botW / 2 - inset, h / 2 - inset), P(-botW / 2 + inset, h / 2 - inset), Beer);
            dl.AddLine(P(-wTop / 2 + inset, top), P(wTop / 2 - inset, top), Foam, MathF.Max(2, h * 0.06f));
        }

        // White rim band, like a party cup.
        var band = h * 0.14f;
        var bandW = Lerp(rimW, botW, band / h);
        dl.AddQuadFilled(rimL, rimR, P(bandW / 2, -h / 2 + band), P(-bandW / 2, -h / 2 + band),
                         dimmed ? WithAlpha(RimColor, 110) : RimColor);

        dl.AddQuad(rimL, rimR, botR, botL, Outline, 1.5f);

        DrawRing(dl, cup, centre, h);
    }

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

    private static Vector2 Rotate(Vector2 v, float a)
    {
        var (s, c) = MathF.SinCos(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FlipCup.Sync;

namespace FlipCup;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private System.Collections.Generic.List<FurnitureCups.NearbyFurniture>? nearby;

    public MainWindow(Plugin plugin) : base("Flip Cup###FlipCupMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 300), MaximumSize = new Vector2(900, 1200) };
    }

    public override void Draw()
    {
        var cfg = plugin.Config;
        var game = plugin.Game;
        var changed = false;

        DrawOnline();
        ImGui.Separator();

        if (plugin.Session.Role == Role.Guest)
        {
            // Guests use the host's table; just show how the cups are doing locally.
            ImGui.TextWrapped("The host sets up the table and rules. Your plugin uses the same cups automatically.");
            if (plugin.BindStatus.Length > 0)
                ImGui.TextColored(plugin.Furniture.IsBound ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.7f, 0.3f, 1), plugin.BindStatus);
            if (ImGui.Button(plugin.Overlay.Visible ? "Hide cups" : "Show cups"))
                plugin.Overlay.Visible = !plugin.Overlay.Visible;
            if (game.Winner >= 0)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1), $"{game.TeamName(game.Winner)} won! Waiting for the host to start a new round.");
            return;
        }

        // ---- Setup ----
        ImGui.TextWrapped("Drawn cups: stand at one END of the counter facing along it, click 'Place cups here', then use the Position sliders. " +
                          "Real cups: see below.");
        if (ImGui.Button("Place cups here"))
            plugin.SetAnchorToPlayer();
        ImGui.SameLine();
        if (ImGui.Button(plugin.Overlay.Visible ? "Hide cups" : "Show cups"))
            plugin.Overlay.Visible = !plugin.Overlay.Visible;
        ImGui.SameLine();
        if (ImGui.Button("New round"))
            plugin.NewRound();

        if (!cfg.HasAnchor)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "No cups placed yet.");

        // ---- Real in-game cups ----
        if (ImGui.CollapsingHeader("Use real cups (furniture)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Place 8 cup furnishings on the counter in two rows of 4 (Tankard works best). " +
                              "Stand next to the counter, scan, click your cup, then 'Use as cups'. " +
                              "Seat 1 is the end of each row nearest you. " +
                              "Flips only move the models on your screen; nothing is saved to the house.");

            if (ImGui.Button("Scan nearby furniture"))
                nearby = plugin.Furniture.ScanNearby(Plugin.ObjectTable.LocalPlayer?.Position ?? default);
            if (nearby != null)
            {
                if (nearby.Count == 0) ImGui.TextDisabled("Nothing found near you (are you inside the house?).");
                else ImGui.TextDisabled("Click the cups (look for x8):");
                foreach (var n in nearby.Take(25))
                {
                    var label = $"{n.Name}  x{n.Count}   ({n.Closest:F1}y away, id {n.Key})##k{n.Key}";
                    if (ImGui.Selectable(label, n.Key == cfg.FurnitureBaseId))
                    {
                        cfg.FurnitureBaseId = n.Key;
                        cfg.FurnitureName = n.Name;
                        changed = true;
                    }
                }
            }

            var fname = cfg.FurnitureName;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("Cup furnishing", ref fname, 64))
            {
                cfg.FurnitureName = fname;
                cfg.FurnitureBaseId = 0;   // typed a name: match by name instead of the picked id
                changed = true;
            }
            if (ImGui.Button("Write debug file"))
                plugin.WriteDebugDump();

            if (ImGui.Button("Use as cups"))
                plugin.BindFurniture();
            if (plugin.Furniture.IsBound)
            {
                ImGui.SameLine();
                if (ImGui.Button("Stop using furniture"))
                    plugin.UnbindFurniture();
            }
            if (plugin.BindStatus.Length > 0)
                ImGui.TextColored(plugin.Furniture.IsBound ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.7f, 0.3f, 1), plugin.BindStatus);

            changed |= Slider("Cup model height", cfg.ModelHeight, v => cfg.ModelHeight = v, 0.05f, 1f);
            ImGui.TextDisabled("If a flipped cup sinks into or floats above the counter, adjust this.");
            changed |= Slider("Search radius", cfg.SearchRadius, v => cfg.SearchRadius = v, 2f, 20f);
        }

        if (ImGui.CollapsingHeader(plugin.Furniture.IsBound ? "Position (drawn cups only)" : "Position",
                                   plugin.Furniture.IsBound ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= Slider("Counter height", cfg.HeightOffset, v => cfg.HeightOffset = v, -1f, 3f);
            changed |= Slider("Distance to first cups", cfg.StartDistance, v => cfg.StartDistance = v, 0f, 5f);
            changed |= Slider("Cup spacing", cfg.Spacing, v => cfg.Spacing = v, 0.1f, 2f);
            changed |= Slider("Row separation", cfg.SideOffset, v => cfg.SideOffset = v, 0.05f, 2f);
            changed |= Slider("Cup size", cfg.CupHeight, v => cfg.CupHeight = v, 0.05f, 1f);
            var deg = cfg.AnchorRotation * 180f / MathF.PI;
            if (ImGui.SliderFloat("Rotate layout", ref deg, -180f, 180f, "%.0fÂ°"))
            {
                cfg.AnchorRotation = deg * MathF.PI / 180f;
                changed = true;
            }
        }

        // ---- Rules ----
        if (ImGui.CollapsingHeader("Rules"))
        {
            var chug = cfg.RequireChug;
            if (ImGui.Checkbox("Click to chug before flipping", ref chug)) { cfg.RequireChug = chug; changed = true; }
            var order = cfg.EnforceTurnOrder;
            if (ImGui.Checkbox("Relay order (next seat waits for the previous flip)", ref order)) { cfg.EnforceTurnOrder = order; changed = true; }
            var pct = cfg.FlipChance * 100f;
            if (ImGui.SliderFloat("Flip success chance", ref pct, 5f, 95f, "%.0f%%")) { cfg.FlipChance = pct / 100f; changed = true; }
        }

        // ---- Teams ----
        if (ImGui.CollapsingHeader("Teams & players", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var t = 0; t < 2; t++)
            {
                ImGui.PushID(t);
                var tn = cfg.TeamNames[t] ?? "";
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputText(t == 0 ? "Left row" : "Right row", ref tn, 32)) { cfg.TeamNames[t] = tn; changed = true; }

                ImGui.Indent();
                for (var s = 0; s < Game.SeatsPerTeam; s++)
                {
                    var i = t * Game.SeatsPerTeam + s;
                    var pn = cfg.PlayerNames[i] ?? "";
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.InputText($"Seat {s + 1}##p{i}", ref pn, 32)) { cfg.PlayerNames[i] = pn; changed = true; }
                    var cup = game.Cups[i];
                    ImGui.SameLine();
                    ImGui.TextDisabled(cup.State == CupState.Down ? $"done ({cup.Attempts} flips)" : cup.State.ToString());
                }
                ImGui.Unindent();
                ImGui.PopID();
            }
        }

        if (game.Winner >= 0)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1), $"{game.TeamName(game.Winner)} won! Press 'New game' to play again.");

        if (changed)
            plugin.SaveConfig();
    }

    private string joinCode = "";

    private void DrawOnline()
    {
        var s = plugin.Session;
        var game = plugin.Game;

        if (!ImGui.CollapsingHeader("Online (play with friends)", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (s.Role == Role.Offline)
        {
            if (s.Error.Length > 0) ImGui.TextColored(Red, s.Error);
            ImGui.TextWrapped("Everyone needs this plugin and must be in the same house.");
            ImGui.Spacing();

            // --- Hosting ---
            ImGui.Separator();
            ImGui.TextColored(Gold, "HOST A GAME");
            ImGui.TextWrapped("Pick this if the cups are on YOUR counter. You set up the table and rules, and get a code to share.");
            if (ImGui.Button("Host a game", new Vector2(160, 0)))
                plugin.HostOnline();

            // --- Joining: clearly separate ---
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(Gold, "JOIN A FRIEND'S GAME");
            ImGui.TextWrapped("Pick this if a friend is hosting. Type the 6-letter code they gave you.");
            ImGui.SetNextItemWidth(110);
            ImGui.InputTextWithHint("##code", "CODE", ref joinCode, 8, ImGuiInputTextFlags.CharsUppercase);
            ImGui.SameLine();
            if (ImGui.Button("Join game") && joinCode.Trim().Length > 0)
                plugin.JoinOnline(joinCode);
            ImGui.Separator();
            return;
        }

        // Connected (or trying to be).
        var ok = s.Connected && s.Error.Length == 0;
        ImGui.TextColored(ok ? Green : Orange,
            s.Role == Role.Host ? $"You're hosting - {s.Status}" : $"Playing in {s.HostName}'s game - {s.Status}");
        if (s.Error.Length > 0) ImGui.TextColored(Red, s.Error);

        ImGui.Text("Game code:");
        ImGui.SameLine();
        ImGui.TextColored(Gold, s.Code);
        if (s.Role == Role.Host)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy join command"))
                ImGui.SetClipboardText($"/flipcup join {s.Code}");
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Leave game"))
                plugin.LeaveOnline();
        }

        // Seat picker: one row per team.
        ImGui.TextDisabled("Pick your seat (click a free one). You can only flip your own cup.");
        var mySeat = s.MySeat;
        for (var t = 0; t < 2; t++)
        {
            ImGui.Text(game.TeamName(t) + ":");
            for (var seat = 0; seat < Game.SeatsPerTeam; seat++)
            {
                var i = t * Game.SeatsPerTeam + seat;
                var cup = game.Cups[i];
                ImGui.SameLine();
                var label = cup.Owner == null ? $"Seat {seat + 1}\n(free)"
                          : cup.Owner == s.ClientId ? $"Seat {seat + 1}\nYOU"
                          : $"Seat {seat + 1}\n{Shorten(cup.OwnerName)}";
                var taken = cup.Owner != null && cup.Owner != s.ClientId;
                if (taken) ImGui.BeginDisabled();
                if (ImGui.Button($"{label}##seat{i}", new Vector2(80, 38)) && cup.Owner == null)
                    s.ClaimSeat(i);
                if (taken) ImGui.EndDisabled();
            }
        }
        if (mySeat >= 0 && ImGui.SmallButton("Stand up (leave seat)"))
            s.LeaveSeat();

        if (s.Role == Role.Host)
            DrawHostControls();
    }

    private string? confirmKickId;
    private string confirmKickName = "";

    /// <summary>Host only: who's in the game, with Move/Kick, and ending the game.</summary>
    private void DrawHostControls()
    {
        var s = plugin.Session;
        var game = plugin.Game;

        ImGui.Spacing();
        ImGui.TextColored(Gold, "PLAYERS");
        var list = s.Players();
        ImGui.Text($"  {s.MyName} (you, host)");
        if (list.Count == 0) ImGui.TextDisabled("  Nobody has joined yet. Share the code!");

        foreach (var p in list)
        {
            ImGui.PushID(p.Id);
            var where = p.Seat < 0 ? "no seat yet"
                      : $"{game.TeamName(p.Seat / Game.SeatsPerTeam)} seat {p.Seat % Game.SeatsPerTeam + 1}";
            ImGui.Text($"  {p.Name}");
            ImGui.SameLine(200);
            ImGui.TextDisabled(where);
            ImGui.SameLine(360);
            if (p.Seat >= 0 && ImGui.SmallButton("Unseat"))
                lock (game) game.FreeSeat(p.Seat);
            ImGui.SameLine(420);
            if (ImGui.SmallButton("Kick"))
            {
                confirmKickId = p.Id;
                confirmKickName = p.Name;
                ImGui.OpenPopup("confirmkick");
            }
            if (ImGui.BeginPopup("confirmkick"))
            {
                ImGui.Text($"Kick {confirmKickName} from the game?");
                ImGui.TextDisabled("They lose their seat and can't rejoin this game.");
                if (ImGui.Button("Kick them") && confirmKickId != null)
                {
                    s.Kick(confirmKickId);
                    plugin.Announce($"{confirmKickName} was removed from the game.");
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        ImGui.TextDisabled("Unseat frees their cup; Kick removes them from the game. Empty seats can be clicked by anyone.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.2f, 0.2f, 1));
        if (ImGui.Button("End game for everyone"))
            ImGui.OpenPopup("confirmend");
        ImGui.PopStyleColor(2);
        if (ImGui.BeginPopup("confirmend"))
        {
            ImGui.Text("End the game? Everyone is disconnected.");
            if (ImGui.Button("End game"))
            {
                plugin.LeaveOnline();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static readonly Vector4 Gold = new(1, 0.85f, 0.4f, 1);
    private static readonly Vector4 Green = new(0.5f, 1, 0.5f, 1);
    private static readonly Vector4 Orange = new(1, 0.7f, 0.3f, 1);
    private static readonly Vector4 Red = new(1, 0.5f, 0.4f, 1);

    private static string Shorten(string? name) =>
        string.IsNullOrEmpty(name) ? "?" : name.Length > 10 ? name[..10] : name;

    private static bool Slider(string label, float value, Action<float> set, float min, float max)
    {
        if (!ImGui.SliderFloat(label, ref value, min, max, "%.2f")) return false;
        set(value);
        return true;
    }
}

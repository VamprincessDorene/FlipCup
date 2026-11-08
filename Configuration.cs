using Dalamud.Configuration;

namespace FlipCup;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Table anchor: where the player stood (feet position) and which way they faced.
    public bool HasAnchor { get; set; }
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public float AnchorZ { get; set; }
    public float AnchorRotation { get; set; }

    // Layout, in yalms, relative to the anchor.
    public float HeightOffset { get; set; } = 0.95f;   // counter height above your feet
    public float StartDistance { get; set; } = 0.6f;   // distance from anchor to the first pair of cups
    public float Spacing { get; set; } = 0.45f;        // distance between cups along the counter
    public float SideOffset { get; set; } = 0.35f;     // distance from the counter's centre line to each team's row
    public float CupHeight { get; set; } = 0.22f;      // visual size of the cup

    // Real furniture cups: the name of the placed furnishing to use as cups (e.g. "Tankard").
    public string FurnitureName { get; set; } = "Tankard";
    public uint FurnitureBaseId { get; set; }           // HousingFurniture row, set when picked from the scan list; 0 = match by name
    public float ModelHeight { get; set; } = 0.2f;     // real height of that model, used as the flip pivot
    public float SearchRadius { get; set; } = 8f;      // how far from the anchor to look for them
    public bool ReverseSeatOrder { get; set; }         // seat 1 at the other end of the counter
    public bool SwapTeamSides { get; set; }            // teams switch rows

    // Rules.
    public bool RequireChug { get; set; } = true;      // first click = chug, then clicks = flip attempts
    public bool EnforceTurnOrder { get; set; } = true; // seat 2 can't go until seat 1 lands, etc.
    public float FlipChance { get; set; } = 0.5f;

    // Online play: the relay that passes messages between players (free public MQTT broker).
    // Everyone in a game must use the same relay.
    public string RelayHost { get; set; } = "broker.emqx.io";
    public int RelayPort { get; set; } = 8883;

    public string[] TeamNames { get; set; } = { "Team Red", "Team Blue" };
    public string[] PlayerNames { get; set; } = new string[8];
}

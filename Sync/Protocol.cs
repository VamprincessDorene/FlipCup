using System;
using System.Text.Json;

namespace FlipCup.Sync;

// Everything sent over the relay. The host is the referee: guests only send requests,
// and the host broadcasts the full game state (retained, so late joiners get it immediately).
//
// Topics:  ffxiv-flipcup/v1/<CODE>/state   host -> everyone (retained snapshot)
//          ffxiv-flipcup/v1/<CODE>/req     guests -> host

public sealed class CupDto
{
    public int State { get; set; }
    public bool WillLand { get; set; }
    public int Attempts { get; set; }
    public int FlipId { get; set; }
    public string? Owner { get; set; }
    public string? OwnerName { get; set; }
}

public sealed class LayoutDto
{
    // Table centre, flip axis and model height, so everyone animates the cups the same way.
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public float AnchorZ { get; set; }
    public float AnchorRotation { get; set; }
    public bool HasAnchor { get; set; }
    public float ModelHeight { get; set; }

    // Real furniture cups: which item, and where each seat's cup is (8 x XYZ), so every
    // player binds the same physical cup to the same seat.
    public uint FurnitureBaseId { get; set; }
    public string FurnitureName { get; set; } = "";
    public float[]? CupPositions { get; set; }
}

public sealed class StateDto
{
    public int Seq { get; set; }
    public int GameId { get; set; }
    public int Winner { get; set; } = -1;
    public CupDto[] Cups { get; set; } = Array.Empty<CupDto>();
    public string[] TeamNames { get; set; } = Array.Empty<string>();
    public bool RequireChug { get; set; }
    public bool EnforceTurnOrder { get; set; }
    public string HostName { get; set; } = "";
    public LayoutDto Layout { get; set; } = new();
    public string[] Kicked { get; set; } = Array.Empty<string>();   // client ids the host removed
}

public sealed class RequestDto
{
    public string Type { get; set; } = "";   // hello | claim | leave | click
    public string From { get; set; } = "";
    public string Name { get; set; } = "";
    public int Seat { get; set; } = -1;      // cup index 0-7
}

public static class Protocol
{
    public const string Version = "v1";
    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I mix-ups

    public static string NewCode()
    {
        Span<char> c = stackalloc char[6];
        for (var i = 0; i < c.Length; i++) c[i] = CodeChars[Random.Shared.Next(CodeChars.Length)];
        return new string(c);
    }

    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant().Replace(" ", "");

    public static string Root(string code) => $"ffxiv-flipcup/{Version}/{code}";
    public static string StateTopic(string code) => Root(code) + "/state";
    public static string RequestTopic(string code) => Root(code) + "/req";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string text) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(text, Json); }
        catch (JsonException) { return null; }
    }
}

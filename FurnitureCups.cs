using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using HousingFurnitureSheet = Lumina.Excel.Sheets.HousingFurniture;

namespace FlipCup;

/// <summary>
/// Uses real placed furnishings (e.g. 8 Tankards on a counter) as the cups.
/// Flips are purely visual and local: we only change where *your* client draws the model.
/// Nothing is sent to the server, the furniture's saved position never changes, and the
/// original transform is restored on reset, unbind, zone change and plugin unload.
/// </summary>
public sealed unsafe class FurnitureCups : IDisposable
{
    private const uint FurnitureRowBase = 0x30000; // HousingFurniture rows start here

    private readonly Configuration config;
    private readonly IDataManager data;

    private readonly nint[] bound = new nint[Game.SeatsPerTeam * 2];       // HousingObject*
    private readonly Transform[] original = new Transform[Game.SeatsPerTeam * 2];
    private readonly bool[] moved = new bool[Game.SeatsPerTeam * 2];

    public bool IsBound { get; private set; }

    public FurnitureCups(Configuration config, IDataManager data)
    {
        this.config = config;
        this.data = data;
    }

    /// <summary>One kind of furnishing near the anchor. Key identifies the item type; Name is best-effort.</summary>
    public record NearbyFurniture(uint Key, string Name, int Count, float Closest);

    // ----- discovery ----------------------------------------------------------

    private List<nint> PlacedObjects()
    {
        var list = new List<nint>();
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return list;
        foreach (var p in hm->IndoorTerritory->FurnitureManager.ObjectManager.ObjectArray.Objects)
        {
            var go = p.Value;
            if (go != null) list.Add((nint)go);
        }
        return list;
    }

    /// <summary>The placed-furniture record behind a housing object, if the game links one.</summary>
    private static HousingFurniture? FurnitureEntry(nint obj)
    {
        var ho = (HousingObject*)obj;
        var idx = ho->HousingFurnitureIndex;
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return null;
        var mem = hm->IndoorTerritory->FurnitureManager.FurnitureMemory;
        if (idx < 0 || idx >= mem.Length) return null;
        var e = mem[idx];
        return e.Id != 0 ? e : null;
    }

    /// <summary>
    /// The item type: the HousingFurniture sheet row (e.g. 197611 = Tankard). Same for every copy,
    /// regardless of dye. (The furniture record's Id has extra flag bits mixed in, so we don't use it.)
    /// </summary>
    public static uint KeyOf(nint obj) => ((HousingObject*)obj)->BaseId;

    private Dictionary<uint, uint>? modelKeyToRow;

    /// <summary>Best-effort item name for a furniture ID, trying each way the game might encode it.</summary>
    public string NameOfKey(uint id)
    {
        foreach (var (name, _) in NameCandidates(id))
            return name;
        return $"Unknown furnishing #{id}";
    }

    /// <summary>All plausible readings of an ID, most likely first (also used by the debug dump).</summary>
    public IEnumerable<(string Name, string How)> NameCandidates(uint id)
    {
        var sheet = data.GetExcelSheet<HousingFurnitureSheet>();
        modelKeyToRow ??= sheet.GroupBy(r => (uint)r.ModelKey).ToDictionary(g => g.Key, g => g.First().RowId);

        string? NameOfRow(uint row) =>
            sheet.GetRowOrDefault(row)?.Item.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : null;

        if (NameOfRow(id) is { } a) yield return (a, "row");
        if (id < FurnitureRowBase)
        {
            if (NameOfRow(id + FurnitureRowBase) is { } b) yield return (b, "row+0x30000");
            if (modelKeyToRow.TryGetValue(id, out var r) && NameOfRow(r) is { } c) yield return (c, "modelkey");
        }
    }

    public string NameOf(nint obj) => NameOfKey(KeyOf(obj));

    private static Vector3 Pos(nint obj) => ((HousingObject*)obj)->Position;

    private Vector3 Anchor => new(config.AnchorX, config.AnchorY, config.AnchorZ);

    /// <summary>Every kind of furnishing placed near you, items with 8+ copies first.</summary>
    public List<NearbyFurniture> ScanNearby(Vector3 playerPos)
    {
        return PlacedObjects()
            .Select(o => (Key: KeyOf(o), Dist: Vector3.Distance(Pos(o), playerPos)))
            .Where(x => x.Dist <= config.SearchRadius)
            .GroupBy(x => x.Key)
            .Select(g => new NearbyFurniture(g.Key, NameOfKey(g.Key), g.Count(), g.Min(x => x.Dist)))
            .OrderByDescending(x => x.Count >= 8)   // anything with 8+ copies is probably the cups
            .ThenBy(x => x.Closest)
            .ToList();
    }

    /// <summary>Writes everything we can see about nearby furniture to a text file for troubleshooting.</summary>
    public string DebugDump(Vector3 playerPos)
    {
        var sb = new System.Text.StringBuilder();
        var hm = HousingManager.Instance();
        sb.AppendLine($"HousingManager={(nint)hm:X} Indoor={(hm == null ? 0 : (nint)hm->IndoorTerritory):X}");
        var objs = PlacedObjects();
        sb.AppendLine($"Objects in array: {objs.Count}");
        if (hm != null && hm->IndoorTerritory != null)
        {
            var mem = hm->IndoorTerritory->FurnitureManager.FurnitureMemory;
            var used = 0;
            foreach (var e in mem) if (e.Id != 0) used++;
            sb.AppendLine($"Furniture records: {used} of {mem.Length}");
        }
        sb.AppendLine($"Player {playerPos}  Anchor {(config.HasAnchor ? Anchor.ToString() : "not set")}");
        sb.AppendLine();
        foreach (var o in objs.OrderBy(o => Vector3.Distance(Pos(o), playerPos)).Take(40))
        {
            var ho = (HousingObject*)o;
            var entry = FurnitureEntry(o);
            var names = string.Join(" | ", NameCandidates(KeyOf(o)).Select(c => $"{c.How}={c.Name}"));
            sb.AppendLine($"{Vector3.Distance(Pos(o), playerPos),6:F2}y  key={KeyOf(o)} base={ho->BaseId} " +
                          $"furnIdx={ho->HousingFurnitureIndex} furnId={entry?.Id ?? 0} " +
                          $"kind={ho->ObjectKind} sg={(ho->SharedGroupLayoutInstance != null)} pos={Pos(o)}  {names}");
        }
        return sb.ToString();
    }

    // ----- binding --------------------------------------------------------------

    /// <summary>
    /// Finds the 8 matching cups nearest you and works out the layout from the cups themselves:
    /// the long direction of the group is the counter, the two rows are the teams, and seat 1 is
    /// the end of each row closest to you. You can stand anywhere near the counter.
    /// Returns an error message, or null on success.
    /// </summary>
    public string? Bind(Vector3 playerPos)
    {
        Unbind();

        bool IsCup(nint o) => config.FurnitureBaseId != 0
            ? KeyOf(o) == config.FurnitureBaseId
            : string.Equals(NameOf(o), config.FurnitureName, StringComparison.OrdinalIgnoreCase);

        var matches = PlacedObjects()
            .Where(IsCup)
            .Where(o => Vector3.Distance(Pos(o), playerPos) <= config.SearchRadius)
            .OrderBy(o => Vector3.Distance(Pos(o), playerPos))
            .Take(8)
            .ToList();
        if (matches.Count < 8)
            return $"Found {matches.Count} \"{config.FurnitureName}\" within {config.SearchRadius:F0} yalms of you; need 8. " +
                   "Stand next to the counter and try again.";

        // Principal axis of the 8 cups on the floor plane = the counter's long direction.
        var mean = matches.Aggregate(Vector3.Zero, (a, o) => a + Pos(o)) / matches.Count;
        float sxx = 0, szz = 0, sxz = 0;
        foreach (var o in matches)
        {
            var d = Pos(o) - mean;
            sxx += d.X * d.X; szz += d.Z * d.Z; sxz += d.X * d.Z;
        }
        var theta = 0.5f * MathF.Atan2(2 * sxz, sxx - szz);
        var along = new Vector3(MathF.Cos(theta), 0, MathF.Sin(theta));
        if (Vector3.Dot(playerPos - mean, along) > 0) along = -along;   // seat 1 = the end nearest you
        var across = new Vector3(-along.Z, 0, along.X);

        var byRow = matches.OrderBy(o => Vector3.Dot(Pos(o) - mean, across)).ToList();
        var rows = new[] { byRow.Take(4).ToList(), byRow.Skip(4).ToList() };

        for (var t = 0; t < 2; t++)
        {
            var ordered = rows[t].OrderBy(o => Vector3.Dot(Pos(o) - mean, along)).ToList();
            for (var s = 0; s < Game.SeatsPerTeam; s++)
            {
                var i = t * Game.SeatsPerTeam + s;
                var ho = (HousingObject*)ordered[s];
                if (ho->SharedGroupLayoutInstance == null)
                {
                    Forget();
                    return "One of the cups hasn't finished loading. Try again in a moment.";
                }
                bound[i] = ordered[s];
                original[i] = ho->SharedGroupLayoutInstance->Transform;
            }
        }

        // Store the layout as the anchor so the flip axis runs along the counter.
        config.AnchorX = mean.X; config.AnchorY = mean.Y; config.AnchorZ = mean.Z;
        config.AnchorRotation = MathF.Atan2(along.X, along.Z);
        config.HasAnchor = true;

        IsBound = true;
        return null;
    }

    /// <summary>
    /// Online guest: bind exactly the cups the host is using, seat by seat, by position.
    /// Everyone in the same house sees furniture at the same coordinates.
    /// </summary>
    public string? BindToPositions(uint baseId, Vector3[] seatPositions)
    {
        Unbind();
        var candidates = PlacedObjects().Where(o => KeyOf(o) == baseId).ToList();
        for (var i = 0; i < bound.Length; i++)
        {
            var best = candidates.OrderBy(o => Vector3.Distance(Pos(o), seatPositions[i])).FirstOrDefault();
            if (best == 0 || Vector3.Distance(Pos(best), seatPositions[i]) > 0.3f)
            {
                Forget();
                return "Couldn't find the host's cups here. Are you in the same house, next to the counter?";
            }
            var ho = (HousingObject*)best;
            if (ho->SharedGroupLayoutInstance == null)
            {
                Forget();
                return "The cups haven't finished loading yet.";
            }
            bound[i] = best;
            original[i] = ho->SharedGroupLayoutInstance->Transform;
            candidates.Remove(best);
        }
        IsBound = true;
        return null;
    }

    public void Unbind()
    {
        if (IsBound) RestoreAll();
        Forget();
    }

    /// <summary>Called when the zone changes: the objects are gone, so just forget them.</summary>
    public void Forget()
    {
        IsBound = false;
        Array.Clear(bound);
        Array.Clear(moved);
    }

    public Vector3 RestPosition(int i) => original[i].Translation;

    /// <summary>Item type of the bound cups (game thread only).</summary>
    public uint BoundBaseId => IsBound ? KeyOf(bound[0]) : 0;

    // ----- animation ------------------------------------------------------------

    /// <summary>Must run on the game's framework thread.</summary>
    public void Apply(Game game)
    {
        if (!IsBound) return;

        // Make sure every cup still exists (furniture can be removed or the house can unload).
        var live = PlacedObjects().ToHashSet();
        if (bound.Any(b => !live.Contains(b)))
        {
            Forget();
            return;
        }

        var r = config.AnchorRotation;
        var axis = new Vector3(MathF.Sin(r), 0, MathF.Cos(r)); // flip around the counter's long axis
        var up = Vector3.UnitY;
        var h = config.ModelHeight;

        for (var i = 0; i < bound.Length; i++)
        {
            var cup = game.Cups[i];
            var (angle, lift) = cup.Pose();

            // Only touch the model while it's moving or displaced, so we never fight the housing editor.
            var atRest = angle == 0 && lift == 0;
            if (atRest && !moved[i]) continue;
            moved[i] = !atRest;

            var sign = cup.Team == 0 ? 1f : -1f; // both rows flip toward the middle of the counter

            var q = Quaternion.CreateFromAxisAngle(axis, sign * angle);
            var o = original[i];
            var t = o;

            // Rotate about the cup's centre (not its base) so rim-down lands flush on the counter.
            Vector3 basePos = o.Translation;
            var pivot = basePos + up * (h / 2);
            var pos = pivot + Vector3.Transform(-up * (h / 2), q) + up * (lift * h);
            if (cup.State == CupState.OnSide) pos -= up * (h * 0.15f); // lying down sits a bit lower

            t.Translation = pos;
            t.Rotation = Quaternion.Concatenate(o.Rotation, q);
            SetTransform(i, t);
        }
    }

    private void RestoreAll()
    {
        for (var i = 0; i < bound.Length; i++)
            if (bound[i] != 0) SetTransform(i, original[i]);
    }

    private void SetTransform(int i, Transform t)
    {
        var ho = (HousingObject*)bound[i];
        var sg = ho->SharedGroupLayoutInstance;
        if (sg != null) sg->SetTransform(&t);
    }

    public void Dispose() => Unbind();
}

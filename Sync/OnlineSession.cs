using System;
using System.Linq;
using System.Threading.Tasks;

namespace FlipCup.Sync;

public enum Role { Offline, Host, Guest }

/// <summary>
/// Online play. The host's plugin is the referee: it owns the real game, rolls every 50/50,
/// and broadcasts a snapshot whenever anything changes (plus a heartbeat every few seconds).
/// Guests send requests (sit here / click my cup) and mirror the host's snapshot.
/// Call <see cref="Pump"/> once per frame on the game thread, inside lock(game).
/// </summary>
public sealed class OnlineSession : IDisposable
{
    private const float HeartbeatSeconds = 3f;
    private const float HostTimeoutSeconds = 12f;

    private readonly Configuration config;
    private readonly Game game;
    private readonly Action<string> announce;

    /// <summary>Host: describe the current table layout so guests can match it.</summary>
    public Func<LayoutDto>? GetLayout { get; set; }
    /// <summary>Guest: the host's table layout arrived/changed.</summary>
    public Action<LayoutDto>? ApplyLayout { get; set; }

    private RelayClient? relay;
    private float sinceBroadcast;
    private float sinceHostHeard;
    private int seq;
    private bool gotFirstSnapshot;
    private string lastLayoutJson = "";
    private bool leaveRequested;

    // Host: everyone who has joined (client id -> character name), and who's been kicked.
    private readonly System.Collections.Generic.Dictionary<string, string> players = new();
    private readonly System.Collections.Generic.HashSet<string> kicked = new();

    public record PlayerInfo(string Id, string Name, int Seat, bool Away);

    // Presence: guests check in every few seconds; the host notices when they stop.
    private const float PingSeconds = 5f;
    private const float AwayAfterSeconds = 15f;
    private const float DropAfterSeconds = 45f;
    private float hostClock;
    private float sincePing;
    private float sinceDropCheck;
    private readonly System.Collections.Generic.Dictionary<string, float> lastSeen = new();
    private readonly System.Collections.Generic.HashSet<string> pingers = new();

    public Role Role { get; private set; } = Role.Offline;
    public string Code { get; private set; } = "";
    public string ClientId { get; } = "fc-" + Guid.NewGuid().ToString("N")[..12];
    public string MyName { get; set; } = "Player";
    public string HostName { get; private set; } = "";
    public string Error { get; private set; } = "";

    public bool IsOnline => Role != Role.Offline;
    public bool Connected => relay?.Connected == true;
    public string Status => Role switch
    {
        Role.Offline => "Offline",
        Role.Guest when Connected && !gotFirstSnapshot => "Connected, waiting for the host...",
        Role.Guest when Connected && sinceHostHeard > HostTimeoutSeconds => "Can't hear the host. Did they leave?",
        _ => relay?.Status ?? "Offline",
    };

    public OnlineSession(Configuration config, Game game, Action<string> announce)
    {
        this.config = config;
        this.game = game;
        this.announce = announce;
    }

    // ----- start / stop ---------------------------------------------------------

    public async Task HostAsync()
    {
        await LeaveAsync();
        Code = Protocol.NewCode();
        Role = Role.Host;
        HostName = MyName;
        Error = "";
        relay = new RelayClient();
        try
        {
            await relay.ConnectAsync(config.RelayHost, config.RelayPort, ClientId, Protocol.RequestTopic(Code));
            lock (game) game.Changed = true;   // publish the first snapshot straight away
        }
        catch (Exception ex)
        {
            Error = "Couldn't reach the relay: " + ex.Message;
        }
    }

    public async Task JoinAsync(string code)
    {
        await LeaveAsync();
        Code = Protocol.NormalizeCode(code);
        Role = Role.Guest;
        Error = "";
        gotFirstSnapshot = false;
        sinceHostHeard = 0;
        relay = new RelayClient();
        try
        {
            await relay.ConnectAsync(config.RelayHost, config.RelayPort, ClientId, Protocol.StateTopic(Code));
            await Send(new RequestDto { Type = "hello" });
        }
        catch (Exception ex)
        {
            Error = "Couldn't reach the relay: " + ex.Message;
        }
    }

    public async Task LeaveAsync()
    {
        var r = relay;
        relay = null;
        if (r != null)
        {
            if (Role == Role.Host)
                await r.ClearRetainedAsync(Protocol.StateTopic(Code));   // game over: remove it from the relay
            else if (Role == Role.Guest)
                await SendVia(r, new RequestDto { Type = "leave" });
            await r.DisconnectAsync();
            r.Dispose();
        }
        // Back to a local game: nobody is sitting anywhere any more (host and guests alike).
        lock (game) foreach (var c in game.Cups) { c.Owner = null; c.OwnerName = null; }
        Role = Role.Offline;
        Code = "";
        gotFirstSnapshot = false;
        lastLayoutJson = "";
        leaveRequested = false;
        lock (players) { players.Clear(); kicked.Clear(); lastSeen.Clear(); pingers.Clear(); }
    }

    // ----- host controls ----------------------------------------------------------

    /// <summary>Host: everyone in the game (seated or watching), for the player list.</summary>
    public System.Collections.Generic.List<PlayerInfo> Players()
    {
        lock (players)
            return players.Select(p => new PlayerInfo(p.Key, p.Value, game.SeatOf(p.Key), IsAway(p.Key)))
                          .OrderBy(p => p.Seat < 0 ? 99 : p.Seat).ToList();
    }

    /// <summary>Host: has this player's plugin stopped checking in? (Caller holds the players lock.)</summary>
    private bool IsAway(string id) =>
        pingers.Contains(id) && lastSeen.TryGetValue(id, out var t) && hostClock - t > AwayAfterSeconds;

    /// <summary>Host: take whoever is in this seat out of it (they stay in the game if still connected).</summary>
    public void RemoveFromSeat(int index)
    {
        lock (game) game.FreeSeat(index);
    }

    /// <summary>Host: empty every seat except your own.</summary>
    public void ClearAllSeats()
    {
        lock (game)
            for (var i = 0; i < game.Cups.Length; i++)
                if (game.Cups[i].Owner != null && game.Cups[i].Owner != ClientId)
                    game.FreeSeat(i);
    }

    /// <summary>
    /// Host: drop players whose plugin has gone quiet (game closed, crashed, disconnected)
    /// so they don't hold a seat forever. Only players on a version that checks in are timed out.
    /// </summary>
    private void DropQuietPlayers()
    {
        System.Collections.Generic.List<(string Id, string Name)> gone;
        lock (players)
            gone = pingers.Where(id => lastSeen.TryGetValue(id, out var t) && hostClock - t > DropAfterSeconds)
                          .Select(id => (id, players.TryGetValue(id, out var n) ? n : "A player")).ToList();
        foreach (var (id, name) in gone)
        {
            lock (players) { players.Remove(id); lastSeen.Remove(id); pingers.Remove(id); }
            var hadSeat = game.SeatOf(id) >= 0;
            game.Release(id);
            if (hadSeat) announce($"{name} stopped responding and was removed from their seat.");
        }
    }

    /// <summary>
    /// Host: remove a player. They lose their seat, get disconnected, and the host ignores
    /// anything else they send for the rest of this game.
    /// </summary>
    public void Kick(string clientId)
    {
        if (Role != Role.Host || clientId == ClientId) return;
        lock (game)
        {
            lock (players)
            {
                kicked.Add(clientId);
                players.Remove(clientId);
                lastSeen.Remove(clientId);
                pingers.Remove(clientId);
            }
            game.Release(clientId);
            game.Changed = true;
        }
    }

    // ----- player actions (any thread) --------------------------------------------

    public int MySeat => game.SeatOf(ClientId);

    /// <summary>Can I click this cup? Online, you can click your own cup or an empty seat's cup.</summary>
    public bool CanClick(Cup c)
    {
        if (!game.CanInteract(c)) return false;
        if (!IsOnline) return true;
        return c.Owner == null || c.Owner == ClientId;
    }

    public void Click(Cup c)
    {
        var index = Array.IndexOf(game.Cups, c);
        if (Role == Role.Guest)
        {
            _ = Send(new RequestDto { Type = "click", Seat = index });
            return;
        }
        lock (game)
        {
            if (CanClick(c)) game.Click(c);
        }
    }

    public void ClaimSeat(int index)
    {
        if (Role == Role.Guest) { _ = Send(new RequestDto { Type = "claim", Seat = index }); return; }
        lock (game) game.Claim(index, ClientId, MyName);
    }

    public void LeaveSeat()
    {
        if (Role == Role.Guest) { _ = Send(new RequestDto { Type = "leave" }); return; }
        lock (game) game.Release(ClientId);
    }

    // ----- per-frame ----------------------------------------------------------------

    /// <summary>Game thread, inside lock(game).</summary>
    public void Pump(float dt)
    {
        if (relay == null) return;

        while (relay.Inbox.TryDequeue(out var msg))
        {
            if (Role == Role.Host) HandleRequest(msg.Payload);
            else if (Role == Role.Guest) HandleState(msg.Payload);
        }

        if (Role == Role.Host)
        {
            hostClock += dt;
            sinceDropCheck += dt;
            if (sinceDropCheck >= 1f)
            {
                sinceDropCheck = 0;
                DropQuietPlayers();
            }
            sinceBroadcast += dt;
            if (game.Changed || sinceBroadcast >= HeartbeatSeconds) Broadcast();
        }
        else
        {
            sinceHostHeard += dt;
            sincePing += dt;
            if (sincePing >= PingSeconds && gotFirstSnapshot)
            {
                sincePing = 0;
                _ = Send(new RequestDto { Type = "ping" });   // "I'm still here"
            }
        }

        // Guest: kicked, or the host ended the game -> go offline (the error stays visible).
        if (leaveRequested)
        {
            leaveRequested = false;
            var reason = Error;
            _ = LeaveAsync().ContinueWith(_ => { Error = reason; announce(reason); });
        }
    }

    private void HandleRequest(string payload)
    {
        var r = Protocol.Deserialize<RequestDto>(payload);
        if (r == null || string.IsNullOrEmpty(r.From) || r.From == ClientId) return;

        lock (players)
        {
            if (kicked.Contains(r.From)) return;   // removed by the host: ignore everything
            if (r.Type == "leave")
            {
                players.Remove(r.From);
                lastSeen.Remove(r.From);
                pingers.Remove(r.From);
            }
            else
            {
                players[r.From] = Clean(r.Name);
                lastSeen[r.From] = hostClock;
                if (r.Type == "ping") pingers.Add(r.From);
            }
        }

        switch (r.Type)
        {
            case "hello":
                game.Changed = true;   // re-send the state so they're up to date
                break;
            case "claim":
                if (!game.Claim(r.Seat, r.From, Clean(r.Name)))
                    game.Changed = true; // seat taken: re-send so their screen corrects itself
                break;
            case "leave":
                game.Release(r.From);
                break;
            case "click":
                if (r.Seat < 0 || r.Seat >= game.Cups.Length) break;
                var cup = game.Cups[r.Seat];
                // The host enforces the rules: only your own cup (or an empty seat's).
                if (cup.Owner == null || cup.Owner == r.From) game.Click(cup);
                break;
        }
    }

    private void Broadcast()
    {
        game.Changed = false;
        sinceBroadcast = 0;
        var snap = game.ToSnapshot();
        snap.Seq = ++seq;
        snap.HostName = MyName;
        snap.Layout = GetLayout?.Invoke() ?? new LayoutDto();
        lock (players) snap.Kicked = kicked.ToArray();
        _ = relay!.PublishAsync(Protocol.StateTopic(Code), Protocol.Serialize(snap), retain: true);
    }

    private void HandleState(string payload)
    {
        if (leaveRequested) return;
        if (string.IsNullOrEmpty(payload))
        {
            Error = "The host ended the game.";
            leaveRequested = true;
            return;
        }
        var s = Protocol.Deserialize<StateDto>(payload);
        if (s == null) return;

        if (Array.IndexOf(s.Kicked, ClientId) >= 0)
        {
            Error = "The host removed you from the game.";
            leaveRequested = true;
            return;
        }

        sinceHostHeard = 0;
        HostName = s.HostName;
        game.ApplySnapshot(s, firstSnapshot: !gotFirstSnapshot);
        game.Changed = false;
        gotFirstSnapshot = true;
        Error = "";

        var layoutJson = Protocol.Serialize(s.Layout);
        if (layoutJson != lastLayoutJson)
        {
            lastLayoutJson = layoutJson;
            ApplyLayout?.Invoke(s.Layout);
        }
    }

    // ----- helpers ---------------------------------------------------------------------

    private Task Send(RequestDto r) => relay == null ? Task.CompletedTask : SendVia(relay, r);

    private Task SendVia(RelayClient r, RequestDto req)
    {
        req.From = ClientId;
        req.Name = MyName;
        return r.PublishAsync(Protocol.RequestTopic(Code), Protocol.Serialize(req));
    }

    private static string Clean(string name)
    {
        name = name.Trim();
        return name.Length > 32 ? name[..32] : name;
    }

    public void Dispose()
    {
        try { LeaveAsync().Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort on unload */ }
    }
}

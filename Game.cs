using System;
using System.Linq;

namespace FlipCup;

public enum CupState
{
    Full,      // has a drink in it, waiting to be chugged
    Empty,     // chugged, upright, ready to flip
    Flipping,  // mid-air
    OnSide,    // missed: lying on its side for a moment before being stood back up
    Down,      // landed rim-down: this seat is done
}

public sealed class Cup
{
    public const float FlipDuration = 0.75f;
    public const float ChugDuration = 0.6f;
    public const float OnSideDuration = 0.6f;

    public int Team;
    public int Seat;
    public CupState State = CupState.Full;
    public float Timer;          // seconds into the current animation
    public float ChugTimer;      // drains the liquid after a chug
    public bool WillLand;        // decided when the flip starts
    public int Attempts;
    public int FlipId;           // bumps on every flip, so guests know to start a new animation

    // Online play: who sits at this cup (relay client id + character name). Null = open seat.
    public string? Owner;
    public string? OwnerName;

    /// <summary>
    /// Current pose of the cup. Angle: 0 = upright, PI = rim down, 1.5*PI = on its side.
    /// Lift: hop height in multiples of the cup's height.
    /// </summary>
    public (float Angle, float Lift) Pose()
    {
        switch (State)
        {
            case CupState.Flipping:
            {
                var t = Math.Clamp(Timer / FlipDuration, 0, 1);
                var target = WillLand ? MathF.PI : MathF.PI * 1.5f; // a miss over-rotates onto its side
                var ease = 1 - (1 - t) * (1 - t);
                return (target * ease, MathF.Sin(t * MathF.PI) * 1.3f);
            }
            case CupState.OnSide: return (MathF.PI * 1.5f, 0);
            case CupState.Down:   return (MathF.PI, 0);
            default:              return (0, 0);
        }
    }
}

public sealed class Game
{
    public const int SeatsPerTeam = 4;

    private readonly Configuration config;
    private readonly Action<string> announce;

    public Cup[] Cups { get; } = new Cup[SeatsPerTeam * 2];
    public int Winner { get; private set; } = -1;
    public int GameId { get; private set; }

    /// <summary>Set whenever something other players need to see changes (the host broadcasts then).</summary>
    public bool Changed { get; set; }

    /// <summary>Seconds the winner banner stays up before the cups refill on their own.</summary>
    public const float WinPause = 6f;

    /// <summary>
    /// True when this copy starts the next round itself (offline or host). Guests leave it off
    /// and follow the host's new round.
    /// </summary>
    public bool AutoReset { get; set; } = true;

    /// <summary>Seconds until the next round starts after a win (0 when nobody has won).</summary>
    public float NextRoundIn { get; private set; }

    public Game(Configuration config, Action<string> announce)
    {
        this.config = config;
        this.announce = announce;
        for (var t = 0; t < 2; t++)
            for (var s = 0; s < SeatsPerTeam; s++)
                Cups[t * SeatsPerTeam + s] = new Cup { Team = t, Seat = s };
        Reset();
    }

    /// <summary>New game: refill every cup. Seat owners stay where they are.</summary>
    public void Reset()
    {
        ResetCups();
        GameId++;
        Changed = true;
    }

    private void ResetCups()
    {
        foreach (var c in Cups)
        {
            c.State = config.RequireChug ? CupState.Full : CupState.Empty;
            c.Timer = 0;
            c.ChugTimer = 0;
            c.Attempts = 0;
        }
        Winner = -1;
        NextRoundIn = 0;
    }

    /// <summary>Back to a blank table with nobody seated (used after leaving an online game).</summary>
    public void Clear()
    {
        foreach (var c in Cups) { c.Owner = null; c.OwnerName = null; }
        Reset();
    }

    public string TeamName(int team) =>
        string.IsNullOrWhiteSpace(config.TeamNames[team]) ? $"Team {team + 1}" : config.TeamNames[team];

    public string PlayerName(Cup c)
    {
        if (!string.IsNullOrWhiteSpace(c.OwnerName)) return c.OwnerName;
        var n = config.PlayerNames[c.Team * SeatsPerTeam + c.Seat];
        return string.IsNullOrWhiteSpace(n) ? $"Seat {c.Seat + 1}" : n;
    }

    // ----- seats (online play) -----------------------------------------------

    public int SeatOf(string owner) => Array.FindIndex(Cups, c => c.Owner == owner);

    /// <summary>Sit at a cup. Leaves any other seat you had. Fails if someone else is there.</summary>
    public bool Claim(int index, string owner, string name)
    {
        if (index < 0 || index >= Cups.Length) return false;
        var cup = Cups[index];
        if (cup.Owner != null && cup.Owner != owner) return false;
        Release(owner);
        cup.Owner = owner;
        cup.OwnerName = name;
        Changed = true;
        return true;
    }

    public void Release(string owner)
    {
        foreach (var c in Cups.Where(c => c.Owner == owner))
        {
            c.Owner = null;
            c.OwnerName = null;
            Changed = true;
        }
    }

    public void FreeSeat(int index)
    {
        Cups[index].Owner = null;
        Cups[index].OwnerName = null;
        Changed = true;
    }

    // ----- snapshots (online play) -------------------------------------------

    public Sync.StateDto ToSnapshot() => new()
    {
        GameId = GameId,
        Winner = Winner,
        TeamNames = config.TeamNames,
        RequireChug = config.RequireChug,
        EnforceTurnOrder = config.EnforceTurnOrder,
        Cups = Cups.Select(c => new Sync.CupDto
        {
            // A cup mid-animation is reported by where it's going, so late joiners don't see it hang.
            State = (int)(c.State switch
            {
                CupState.Flipping => c.WillLand ? CupState.Down : CupState.Empty,
                CupState.OnSide => CupState.Empty,
                _ => c.State,
            }),
            WillLand = c.WillLand,
            Attempts = c.Attempts,
            FlipId = c.FlipId,
            Owner = c.Owner,
            OwnerName = c.OwnerName,
        }).ToArray(),
    };

    /// <summary>
    /// Guest side: make our game match the host's. New flips start their animation locally
    /// (outcome already decided by the host), so everyone sees the same thing.
    /// </summary>
    public void ApplySnapshot(Sync.StateDto s, bool firstSnapshot)
    {
        config.RequireChug = s.RequireChug;
        config.EnforceTurnOrder = s.EnforceTurnOrder;
        if (s.TeamNames.Length == 2) config.TeamNames = s.TeamNames;

        if (s.GameId != GameId)
        {
            ResetCups();
            GameId = s.GameId;
        }

        for (var i = 0; i < Cups.Length && i < s.Cups.Length; i++)
        {
            var c = Cups[i];
            var d = s.Cups[i];
            c.Owner = d.Owner;
            c.OwnerName = d.OwnerName;
            var target = (CupState)d.State;

            if (d.FlipId != c.FlipId)
            {
                c.FlipId = d.FlipId;
                c.Attempts = d.Attempts;
                c.WillLand = d.WillLand;
                if (firstSnapshot)
                {
                    c.State = target;   // just joined: don't replay old flips
                }
                else
                {
                    c.State = CupState.Flipping;
                    c.Timer = 0;
                }
                continue;
            }

            if (c.State is CupState.Flipping or CupState.OnSide) continue; // let the animation finish

            if (c.State == CupState.Full && target == CupState.Empty && !firstSnapshot)
                c.ChugTimer = Cup.ChugDuration;
            c.State = target;
            c.Attempts = d.Attempts;
        }

        if (Winner < 0 && s.Winner >= 0 && Cups.All(c => c.State is not (CupState.Flipping or CupState.OnSide)))
        {
            Winner = s.Winner;
            NextRoundIn = WinPause;   // for the countdown display; the host does the actual reset
            if (!firstSnapshot) announce($"{TeamName(Winner)} WINS!");
        }
    }

    /// <summary>The seat currently allowed to go for this team (first cup not yet down).</summary>
    public int ActiveSeat(int team)
    {
        for (var s = 0; s < SeatsPerTeam; s++)
            if (Cups[team * SeatsPerTeam + s].State != CupState.Down)
                return s;
        return SeatsPerTeam;
    }

    public bool CanInteract(Cup c)
    {
        if (Winner >= 0) return false;
        if (c.State is CupState.Flipping or CupState.OnSide or CupState.Down) return false;
        if (config.EnforceTurnOrder && ActiveSeat(c.Team) != c.Seat) return false;
        return true;
    }

    public void Click(Cup c)
    {
        if (!CanInteract(c)) return;

        Changed = true;
        if (c.State == CupState.Full)
        {
            c.State = CupState.Empty;
            c.ChugTimer = Cup.ChugDuration;
            return;
        }

        // Empty -> flip. Coin toss decided now, revealed when it lands.
        c.Attempts++;
        c.FlipId++;
        c.WillLand = Random.Shared.NextSingle() < config.FlipChance;
        c.State = CupState.Flipping;
        c.Timer = 0;
    }

    public void Update(float dt)
    {
        if (Winner >= 0 && NextRoundIn > 0)
        {
            NextRoundIn = MathF.Max(0, NextRoundIn - dt);
            if (NextRoundIn <= 0 && AutoReset)
            {
                Reset();
                announce("New round! Cups are full.");
                return;
            }
        }

        foreach (var c in Cups)
        {
            if (c.ChugTimer > 0) c.ChugTimer = MathF.Max(0, c.ChugTimer - dt);

            switch (c.State)
            {
                case CupState.Flipping:
                    c.Timer += dt;
                    if (c.Timer >= Cup.FlipDuration)
                    {
                        c.Timer = 0;
                        if (c.WillLand)
                        {
                            c.State = CupState.Down;
                            OnLanded(c);
                        }
                        else
                        {
                            c.State = CupState.OnSide;
                        }
                    }
                    break;

                case CupState.OnSide:
                    c.Timer += dt;
                    if (c.Timer >= Cup.OnSideDuration)
                    {
                        c.Timer = 0;
                        c.State = CupState.Empty;
                    }
                    break;
            }
        }
    }

    private void OnLanded(Cup c)
    {
        var tries = c.Attempts == 1 ? "first try!" : $"after {c.Attempts} tries";
        announce($"{PlayerName(c)} ({TeamName(c.Team)}) flipped it {tries}");

        if (Winner < 0 && Cups.Where(x => x.Team == c.Team).All(x => x.State == CupState.Down))
        {
            Winner = c.Team;
            NextRoundIn = WinPause;
            Changed = true;
            announce($"{TeamName(c.Team)} WINS!");
        }
    }
}

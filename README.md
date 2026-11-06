# Flip Cup

A Dalamud plugin for FINAL FANTASY XIV: play flip cup with friends on a counter in your house.

- Place 8 cup furnishings on a counter in two rows of 4 (Tankards work great).
- Click your cup to chug, click again to flip: 50/50 to land rim-down.
- Relay style: each seat waits for the teammate before them. First team to flip all 4 wins.
- One player hosts (`/flipcup host`), friends join with the code (`/flipcup join CODE`) and every flip shows up for everyone.
- Flips only move the cups on players' screens. Nothing is saved to the house and nothing is sent to the game servers.

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, tick **Enabled**, and click **Save**:

   ```
   https://raw.githubusercontent.com/VamprincessDorene/FlipCup/main/repo.json
   ```

3. Type `/xlplugins`, search for **Flip Cup**, and click **Install**.

## Playing

| Command | What it does |
|---|---|
| `/flipcup` | Open the window |
| `/flipcup host` | Host an online game and get a join code |
| `/flipcup join CODE` | Join a friend's game |
| `/flipcup leave` | Leave the online game |
| `/flipcup reset` | New round (host only when online) |

**Host:** stand by the counter, open `/flipcup`, click **Scan nearby furniture**, pick the cups (shown as `x8`), click **Use as cups**, then **Host a game** and share the code.

**Everyone else:** be in the same house, run `/flipcup join CODE`, and click a free seat. You can only flip your own cup.

## How online play works

The host's plugin is the referee: it rolls every flip and broadcasts the game state through a free public MQTT relay (`broker.emqx.io`). Guests send "I clicked my cup" requests. Only character names and game moves are sent. Anyone with the join code can join, so only share it with your group.

## Building

Requires the .NET SDK and XIVLauncher with Dalamud installed. `dotnet build -c Release`, then `.\publish.ps1` to publish a release.

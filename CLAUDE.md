# Pikeman

Naval Battleship-style Unity game. Grid-based fleet combat, currently single-player vs. an AI opponent. Every ship carries a unique perk, so placement and targeting both matter beyond classic Battleship.

Full handoff doc (game explanation, screenshots, ship/perk table) lives in Notion — ask the project owner for the link if you don't have it. This file is the condensed, code-local version for quick orientation.

## Open the project

Install **Unity 6000.5.1f1** via Unity Hub, then open the `pikeman/` sub-folder as the project (not the repo root — the root also holds raw art/asset-dev folders alongside the Unity project). Open `Assets/Scenes/SampleScene.unity` and hit Play.

## How a match works

1. **Setup phase** — drag 5 ships onto a 10×10 grid, rotate with `R`/right-click.
2. **Playing phase** — turns alternate; each turn is one shot OR one perk use, never both. 60s turn timer auto-skips.
3. Two views, toggled anytime: "YOUR FLEET" (your board) and "TARGETING" (radar screen, call shots on the enemy).
4. **Win condition**: sink the enemy's whole fleet first.

Ships & perks (`Assets/Scripts/Battleship/BattleshipPerks.cs`):

| Ship | Len | Perk | Effect |
|---|---|---|---|
| Carrier | 5 | Radar/Scout | Reveals a random undamaged enemy cell |
| Battleship | 4 | Scorch | Main shot + splash hit on an adjacent cell |
| Cruiser | 3 | Intercept (free) | Shields one own cell; counters the next shot landing there |
| Submarine | 3 | Torpedo | Fires down a column, hits first undamaged cell in path |
| Destroyer | 2 | Relocate | Moves the ship; damage travels with it |

Perks get permanently disabled if a specific "control cell" on that ship is hit.

## Tech stack

- Unity 6000.5.1f1, URP 17.5.0
- UI: legacy `OnGUI` only — no Canvas/UGUI or UI Toolkit used, despite those packages being installed
- Input: raw `Event.current`/`Input` polling — new Input System package installed but unused
- No DOTween — all VFX/easing is hand-rolled with `Time.time`
- **Multiplayer: Unity Gaming Services (Lobby + Relay)** — decided 2026-07-24 over Supabase. UGS has an official Unity SDK and matches the already-installed (currently unused) `com.unity.multiplayer.center` package. Nothing implemented yet.

## Architecture

All game code is in `Assets/Scripts/Battleship/` (5 files). `Assets/Scripts/Core/` and `Assets/Scripts/Game/` exist but are empty — unused placeholders, not wired to anything.

- `BattleshipCore.cs` — pure C#, no Unity deps. `Board`: 10×10 grid, placement, fire resolution, relocate-with-traveling-damage, sunk detection.
- `BattleshipPerks.cs` — pure C#. Perk table, cooldowns, disable conditions, per-player perk state.
- `BattleshipAI.cs` — pure C#. 3 difficulty tiers (Easy/random, Medium/hunt-target, Hard/probability heatmap).
- `BattleshipMatch.cs` — pure C#. Orchestrates one match: two boards, two perk states, the AI, turn state machine.
- `BattleshipController.cs` — the only `MonoBehaviour` (3000+ lines). Owns all `OnGUI` rendering, input, VFX, audio, the turn timer, and debug/trailer tooling.

No singleton/event-bus pattern — `BattleshipController` directly drives one `BattleshipMatch`. The pure-C# rules engine has zero Unity dependency, which should make server-authoritative multiplayer easier to wire in later.

Only one scene exists: `Assets/Scenes/SampleScene.unity` (Main Camera, Global Light 2D, BattleshipController). No menu scene, no separate fleet-placement scene — those are just `Phase` states inside `BattleshipController`'s `OnGUI`, not real views yet.

## Known gaps

- No Canvas-based UI anywhere — main menu and fleet placement UI need a real UI system built from scratch, not a tweak.
- No networking code or packages yet — multiplayer is a from-scratch build (see decision above).
- No music/theme channel — only SFX one-shots + one loop channel (`Assets/Resources/Sfx/`, 18 clips).
- New Input System and several 2D packages (Aseprite, Tilemap, SpriteShape) installed but unused.

## Roadmap — remaining

- [ ] Main menu UI
- [ ] Fleet placement UI
- [ ] Multiplayer lobbies (Unity Gaming Services Lobby)
- [ ] 1v1 multiplayer working (Unity Gaming Services Relay)
- [ ] Theme soundtrack
- [ ] Perk balance pass
- [ ] Bug fixing

## Repo

- `Library/` and `builds/` are gitignored — Unity regenerates `Library/` on first open; `builds/` needs rebuilding locally when needed.

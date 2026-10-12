# Elite Dangerous — Macro Deck 3 Plugin

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Macro Deck](https://img.shields.io/badge/Macro%20Deck-3.0%20beta-orange.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)
![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)

A [Macro Deck 3](https://macro-deck.app) plugin that reads Elite Dangerous journals and `Status.json` in real time and exposes the game state as Macro Deck variables.

Build live decks that react to what's happening in your cockpit: current system, docked state, FSD charge, fuel level, shields, hardpoints, mission progress, and much more — all without alt-tabbing out of the game.

## Features

- **Real-time monitoring** of Elite Dangerous journals (`Journal.*.log`) and `Status.json`
- **84 variables** across 14 categories: position, ship, status flags, Odyssey, fuel, cargo, Pips, ranks, progress, combat, missions, exploration, market, commander
- **Incremental journal reading** — only new lines are parsed, no full re-reads
- **Silent startup replay** — no spurious events during the first 3 seconds
- **Debounced write queue** with per-variable coalescing to stay under the host's rate limit
- **Manual resync action** to force a refresh of all variables at any time
- **Offline-safe** — if the journal folder is missing at startup, the plugin retries every 30 seconds
- **Partial-line safe** — journal lines written mid-flush are not lost

## Requirements

- [Macro Deck 3](https://macro-deck.app) (beta)
- Windows 10 or 11 (the journal path is Windows-specific)
- Elite Dangerous (any version) with at least one session played

## Installation

### From a local `.macroDeckPlugin` package

1. Download the latest `.macroDeckPlugin` file from the [Releases](../../releases) page
2. Open **Macro Deck → Plugins**
3. Click **Install from file** and select the `.macroDeckPlugin` file
4. Enable the plugin using the toggle in the top-right corner

### From source

```bash
git clone https://github.com/adallagatacm/EliteDangerousPluginMacrodeck3.git
cd EliteDangerousPluginMacrodeck3
dotnet build
macrodeck-plugin build --source src/EliteDangerousPlugin --output ./artifacts
The resulting .macroDeckPlugin file in artifacts/ can be installed as above.

Usage
Once the plugin is enabled and Elite Dangerous is running, all variables are available in Macro Deck's variable picker. Use them in button labels with the syntax:

text
{{ vars.ed_pos_system }}
Example setups:

Button	Label	Purpose
System tracker	{{ vars.ed_pos_system }}	Shows your current star system
Fuel gauge	{{ vars.ed_fuel_percent }}%	Live fuel percentage
FSD ready	Condition on vars.ed_status_fsd_charging	Green when ready to jump, red when cooling down
Docking helper	Condition on vars.ed_status_docked	Highlights the docking button when docked
Combat alert	Condition on vars.ed_combat_under_attack	Flashes red when under attack
Actions
Resync variables
Forces the plugin to re-read Status.json and the current journal, then republishes every variable.

Useful when the plugin's WebSocket session drops silently and variables stop updating (see Known limitations below). Add this action to a spare button for one-click recovery.

Variables
Position and navigation
Variable	Description	Source
ed_pos_system	Current star system	FSDJump / Location
ed_pos_station	Current station (empty in space)	Docked / Undocked / Location
ed_pos_body	Current celestial body	Status.json (BodyName)
ed_pos_latitude	Latitude on planetary surface	Status.json (Latitude)
ed_pos_longitude	Longitude on planetary surface	Status.json (Longitude)
ed_pos_altitude	Altitude above ground	Status.json (Altitude)
ed_pos_heading	Heading (0–360)	Status.json (Heading)
ed_pos_starpos_x	Galactic X coordinate	FSDJump / Location (StarPos)
ed_pos_starpos_y	Galactic Y coordinate	FSDJump / Location (StarPos)
ed_pos_starpos_z	Galactic Z coordinate	FSDJump / Location (StarPos)
Ship and equipment
Variable	Description	Source
ed_ship_type	Internal ship type (e.g. cobramkv)	LoadGame / Loadout
ed_ship_name	Custom ship name	Loadout (ShipName)
ed_ship_ident	Ship ID plate	Loadout (ShipIdent)
ed_ship_hull_health	Hull health (0.0 – 1.0)	Loadout (HullHealth)
ed_ship_max_jump_range	Maximum jump range (ly)	Loadout (MaxJumpRange)
ed_ship_rebuy_cost	Rebuy cost in credits	Loadout (Rebuy)
ed_ship_module_count	Number of installed modules	Loadout (Modules)
Status flags
Variable	Description	Bit
ed_status_docked	Docked at a station	0
ed_status_landed	Landed on a body	1
ed_status_gear_down	Landing gear deployed	2
ed_status_shields	Shields online	3
ed_status_supercruise	In supercruise	4
ed_status_fa_off	Flight assist disabled	5
ed_status_hardpoints	Hardpoints deployed	6
ed_status_wing	In a wing	7
ed_status_lights	Ship lights on	8
ed_status_cargo_scoop	Cargo scoop deployed	9
ed_status_silent	Silent running	10
ed_status_scooping	Fuel scooping	11
ed_status_fsd_masslock	FSD mass locked	16
ed_status_fsd_charging	FSD charging	17
ed_status_fsd_cooldown	FSD cooldown	18
ed_status_low_fuel	Low fuel warning	19
ed_status_overheat	Overheating	20
ed_status_danger	In danger	22
ed_status_interdicted	Being interdicted	23
ed_status_main_ship	In main ship	24
ed_status_fighter	In fighter	25
ed_status_srv	In SRV	26
Status flags — Odyssey (Flags2)
Variable	Description	Bit
ed_status_on_foot	On foot	0
ed_status_in_taxi	In taxi	1
ed_status_multicrew	In multicrew	2
ed_status_low_oxygen	Low oxygen	6
ed_status_low_health	Low health	7
Fuel
Variable	Description	Source
ed_fuel_main	Main tank fuel (tons)	Status.json
ed_fuel_reservoir	Reservoir fuel (tons)	Status.json
ed_fuel_total	Total fuel (tons)	Computed
ed_fuel_percent	Fuel percentage of capacity	Computed from FuelCapacity
ed_fuel_capacity	Maximum tank capacity	Loadout.FuelCapacity.Main
Cargo
Variable	Description	Source
ed_cargo_tons	Current cargo mass (tons)	Status.json (Cargo)
ed_cargo_percent	Cargo bay fill percentage	Computed from CargoCapacity
ed_cargo_capacity	Maximum cargo capacity	Loadout.CargoCapacity
Pips
Variable	Description	Source
ed_pips_sys	Power to SYS (0–8, half-pip units)	Status.json (Pips[0])
ed_pips_eng	Power to ENG	Status.json (Pips[1])
ed_pips_wep	Power to WEP	Status.json (Pips[2])
Note: values are in half-pip units. Divide by 2 to show whole pips. The sum of the three is always 8 (4 whole pips).

Ranks
Variable	Description	Range	Source
ed_rank_combat	Combat rank	0–8	Rank (Combat)
ed_rank_trade	Trade rank	0–8	Rank (Trade)
ed_rank_explore	Exploration rank	0–8	Rank (Explore)
ed_rank_empire	Empire rank	0–14	Rank (Empire)
ed_rank_federation	Federation rank	0–14	Rank (Federation)
ed_rank_cqc	CQC rank	0–8	Rank (CQC)
Progress
Variable	Description	Range	Source
ed_progress_combat	Progress toward next rank (%)	0–100	Progress
ed_progress_trade	Trade progress	0–100	Progress
ed_progress_explore	Exploration progress	0–100	Progress
ed_progress_empire	Empire progress	0–100	Progress
ed_progress_federation	Federation progress	0–100	Progress
ed_progress_cqc	CQC progress	0–100	Progress
Combat
Variable	Description	Source
ed_combat_under_attack	Under attack (true/false)	UnderAttack
ed_combat_interdicted	Being interdicted	Interdicted
ed_combat_escaped	Escaped interdiction	EscapeInterdiction
ed_combat_last_bounty	Last bounty reward (credits)	Bounty (Reward)
ed_combat_hull_damage	Hull health after damage	HullDamage (Health)
Missions
Variable	Description	Source
ed_mission_count	Number of active missions	Internal counter
ed_mission_last_name	Last accepted mission name	MissionAccepted
ed_mission_last_reward	Last completed mission reward	MissionCompleted (Reward)
Exploration
Variable	Description	Source
ed_explore_last_scan	Last scanned body	Scan (BodyName)
ed_explore_last_scan_type	Type of last scanned body	Scan
ed_explore_last_material	Last collected material	MaterialCollected
ed_explore_fss_bodies	Bodies detected with FSS	FSSDiscoveryScan (BodyCount)
Market
Variable	Description	Source
ed_market_last_profit	Profit from last market sale	MarketSell (TotalSale)
Commander and legal state
Variable	Description	Source
ed_commander_name	Commander name	LoadGame (Commander)
ed_commander_credits	Credits at login	LoadGame (Credits)
ed_legal_state	Legal state (Clean, Wanted, Hostile, …)	Status.json (LegalState)
ed_balance	Current credits (live)	Status.json (Balance)
Known limitations
Session drops after 10–20 minutes. In the current Macro Deck 3 beta, the plugin's WebSocket session sometimes drops silently, causing variables to disappear. Running the Resync variables action restores them immediately. This is a known issue in the beta and is being worked on upstream.

Windows-only journal path. The plugin reads from %USERPROFILE%\Saved Games\Frontier Developments\Elite Dangerous. Proton (Linux) and macOS paths are not supported yet.

Variables are stored as text. The current SDK preview does not accept Numeric or Boolean variable types reliably, so all values are pushed as strings. This will be revisited once Macro Deck 3 leaves beta.

No thousand separators. Credit values are formatted as plain integers (e.g. 2928971), not with locale-specific separators.

Architecture
text
src/EliteDangerousPlugin/
├── Program.cs                 # Host builder entry point
├── PluginIntegration.cs       # IPluginIntegration — wires watcher → variables
├── EliteJournalWatcher.cs     # Reads journal + Status.json, emits typed events
├── EliteStatusModel.cs        # In-memory snapshot of the game state
├── manifest.json              # Plugin identity and per-platform entrypoints
├── macrodeck-build.json       # Build configuration for `macrodeck-plugin build`
├── Localization/Strings.resx  # Localized strings for actions
└── Assets/icon.svg            # Plugin icon
The watcher runs on a background System.Threading.Timer and a FileSystemWatcher, feeding a Channel<(string, string)> that a single writer task drains sequentially. This keeps the host's rate limit happy even during rapid state changes (docking, interdiction, FSD charge).

Building
Requires .NET SDK 10.0 and the Macro Deck Plugin CLI:

bash
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease
dotnet build
dotnet test
macrodeck-plugin build --source src/EliteDangerousPlugin --output ./artifacts
The build produces a framework-dependent plugin that runs on the .NET 10 runtime shipped with Macro Deck 3.

Running against a real host during development
bash
macrodeck-plugin run --project src/EliteDangerousPlugin
The first run will request an enrollment token. Approve the pairing in Macro Deck, and subsequent runs will reuse the stored credential.

Contributing
Pull requests are welcome. For major changes, please open an issue first to discuss what you'd like to change.

Credits
Built on the Macro Deck 3 plugin template.

License
MIT

Macro Deck itself is licensed under Apache 2.0. Elite Dangerous is a trademark of Frontier Developments plc. This plugin is an unofficial community project and is not affiliated with Frontier Developments.
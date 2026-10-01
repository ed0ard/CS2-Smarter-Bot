# CS2-Smarter-Bot
CS2-Smarter-Bot is a plugin based on CounterStrikeSharp that aims to make bots smarter.
# Requirement

[CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)

# Features
1. Keeps bots always active

2. Fixes most bot stuck issues

3. Improves bots' movement

4. Allow bots to spam smoke and tweak bots' vision in smoke to make it more reasonable

5. Each bot has a chance to anti-flash, according to the visible duration of the flash

6. Allows bots to spray at any range

7. Refines bot behavior logic

8. Fixes an issue where bots would only aim without shooting

9. Custom 3D bot perception FOV (Windows and Linux x64)
<img width="464" height="433" alt="smarter" src="https://github.com/user-attachments/assets/43ed231f-a79e-456d-8a25-862d476ccad4" />

# Installation
1. Download the latest **RayTrace-MM.tar.gz** and **RayTrace-CSS-API.tar.gz** from [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)

   Also download the latest **BotController-MM.zip** and **BotController-CSS-API.zip** from [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller/releases)

2. Extract the folders and upload them to `game/csgo/addons` on your server

3. Download the latest **BotState.zip** from [Releases](https://github.com/ed0ard/CS2-Smarter-Bot/releases)

4. Extract the folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server

5. Restart your server

## Rush antenna status

Run `css_rush_antenna` in the player or server console to show the current antenna
owner on `rush_001`: CT, T, or neutral. No admin permission is required, and the
command accepts no arguments. It only replies to the caller and does not change
ownership, entities, convars, or bot behavior.

The command reads the antenna radar marker's color through Schema, as published
by the map script, including during warmup. This reports the displayed antenna
owner, not a predicted round winner. Missing or ambiguous markers and unknown
colors are reported explicitly. The entity is looked up on each query so room
changes and map reloads do not leave a cached owner.

## Custom bot FOV

Custom FOV starts at **120° horizontal and 180° vertical** on Windows and Linux x64.
It changes perception, while the native botprofile-driven view controller, hearing and
decision-making continue to operate normally. Signatures are built into the plugin;
no FOV gamedata file or generated BotState config is required or read.

Server console or an admin with `@css/root` can use:

```text
css_bot_fov 120 180
css_bot_fov 120
css_bot_fov 120 90
css_bot_fov 180
css_bot_fov 360
css_bot_fov native
css_bot_fov status
```

Both inputs are **full angles**: `120 90` means 60 degrees left/right and 45 degrees
up/down in the bot's camera space, including pitch and roll. Each axis accepts 1–180°;
`360` (or `360 360`) enables omnidirectional perception. Mixed 360°/limited axes are rejected.

Two numeric arguments directly set the horizontal and vertical angles. With only one
angle, the command calculates vertical FOV once using a fixed 16:9 ratio; for example,
`css_bot_fov 120` sets approximately 120° × 88.507°. There is no ratio setting or `auto`
argument. `status` (also the no-argument default) displays the two active settings.
Angles must be finite and in range, including the calculated vertical angle.
FOV does not follow spectator screen size or scope zoom. Commands apply for the current
plugin session; reloading starts again at 120° × 180°. Old generated FOV config files
and `custom-fov.gamedata.json` are ignored and can be removed.

Native `IsVisible(player)` retains its target validation, range checks, body sample order
and visible-part mask. Its old center FOV gate is bypassed; each native body sample instead
passes the custom frustum before native blindness, smoke and LOS checks. This remains native
body-point sampling, not full hitbox visibility. Calls explicitly requesting no FOV retain
that behavior, including sound/objective LOS. This is a perception filter, not a hard firing
angle limit for remembered targets or other plugins' aiming systems.

Fake-defuse search keeps the configured FOV while custom mode is enabled, instead of applying
its existing global 360-degree patches. `native` restores the plugin's previous behavior.
Humans, human takeovers and BotController replay/All/Aim ownership are excluded.

The entry signatures include function-specific scope setup and argument saves, with
addresses and stack displacements masked. Both Windows signatures match exactly once
in all 15 supplied May–September binaries and the October 1 local server. Linux signatures
and the player-to-body-point call ABI were verified against the supplied `libserver.so`
(SHA256 `d81faffb3e3a5f2001932b3b55a96c4ac05c2ed4b99702b06fc416b6e9bb5300`).
Linux still needs a live-server smoke test. Missing signatures leave native vision active.
Other plugins that replace these same visibility functions require separate compatibility testing.

Build with the .NET 10 SDK and `BotControllerApi.dll` from the linked BotController project
in `libs/`, then run `dotnet build -c Release`.

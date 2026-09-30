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

9. Optional custom 3D bot perception FOV (Windows)
<img width="464" height="433" alt="smarter" src="https://github.com/user-attachments/assets/43ed231f-a79e-456d-8a25-862d476ccad4" />

# Installation
1. Download the latest **RayTrace-MM.tar.gz** and **RayTrace-CSS-API.tar.gz** from [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)

   Also download the latest **BotController-MM.zip** and **BotController-CSS-API.zip** from [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller/releases)

2. Extract the folders and upload them to `game/csgo/addons` on your server

3. Download the latest **BotState.zip** from [Releases](https://github.com/ed0ard/CS2-Smarter-Bot/releases)

4. Extract the folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server

5. Restart your server

## Custom bot FOV

Custom FOV is **off by default**. It changes perception, while the native botprofile-driven
view controller, hearing and decision-making continue to operate normally. No BotVision
module or native build toolchain is required. Include `custom-fov.gamedata.json` beside
`BotState.dll` when installing or publishing this plugin.

Server console or an admin with `@css/root` can use:

```text
css_bot_fov 120
css_bot_fov 120 90
css_bot_fov 120 auto 1.7777778
css_bot_fov 180
css_bot_fov 360
css_bot_fov native
css_bot_fov status
```

Both inputs are **full angles**: `120 90` means 60 degrees left/right and 45 degrees
up/down in the bot's camera space, including pitch and roll. Each axis accepts 1–180°;
`360` (or `360 360`) enables omnidirectional perception. Mixed 360°/limited axes are rejected.

Omit the vertical angle or use `auto` to derive it from the aspect ratio (default 16:9).
The second numeric argument is now vertical FOV; pass aspect ratio after `auto`.
Explicit vertical FOV ignores aspect ratio. `status` displays both effective angles.
Angles must be finite and in range, including automatic vertical FOV; aspect ratio must
be finite and positive. FOV does not follow spectator screen size or scope zoom.
Commands change the current session. For persistence, edit the CSS-generated BotState config:

```json
{
  "CustomFov": {
    "Enabled": true,
    "HorizontalDegrees": 120,
    "VerticalDegrees": 90,
    "AspectRatio": 1.7777778
  },
  "ConfigVersion": 1
}
```

Set `VerticalDegrees` to `null` (or omit it, as in existing configs) for automatic vertical FOV.
In 360-degree mode it must be `null` or `360`.

Native `IsVisible(player)` retains its target validation, range checks, body sample order
and visible-part mask. Its old center FOV gate is bypassed; each native body sample instead
passes the custom frustum before native blindness, smoke and LOS checks. This remains native
body-point sampling, not full hitbox visibility. Calls explicitly requesting no FOV retain
that behavior, including sound/objective LOS. This is a perception filter, not a hard firing
angle limit for remembered targets or other plugins' aiming systems.

Fake-defuse search keeps the configured FOV while custom mode is enabled, instead of applying
its existing global 360-degree patches. `native` restores the plugin's previous behavior.
Humans, human takeovers and BotController replay/All/Aim ownership are excluded.

The signatures are verified against Windows CS2 1.41.8.5 / revision 11039926. Linux custom
hooks are not included; Linux retains its existing native behavior and reports an unsupported
platform if custom FOV is requested. Missing signatures also leave native vision active.
Other plugins that replace these same visibility functions require separate compatibility testing.

Build with the .NET 10 SDK and `BotControllerApi.dll` from the linked BotController project
in `libs/`, then run `dotnet build -c Release`. Pure geometry and query-scope tests need no
server or BotController binary: `dotnet test tests/BotFov.Tests.csproj -c Release`.

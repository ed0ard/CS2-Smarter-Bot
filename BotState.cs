using CounterStrikeSharp.API.Core;
using System;

namespace BotState;

public partial class BotState : BasePlugin
{
    public override string ModuleName => "Smarter-Bot";
    public override string ModuleVersion => "1.10.0";
    public override string ModuleAuthor => "ed0ard & XBribo & unicbm";
    public override string ModuleDescription => "Make bots smarter";

    private readonly Random _random = new Random();
}

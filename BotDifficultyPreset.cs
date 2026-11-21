using System.Globalization;
using BotControllerApi;

namespace BotState;

internal sealed class BotDifficultyPreset
{
    // Keys follow BotProfileFields bit order.
    private static readonly string[] Keys =
    [
        "Skill", "ReactionTime", "AttackDelay",
        "LookAngleMaxAccelNormal", "LookAngleStiffnessNormal", "LookAngleDampingNormal",
        "LookAngleMaxAccelAttacking", "LookAngleStiffnessAttacking", "LookAngleDampingAttacking",
        "Aggression", "Teamwork", "AimFocusInitial", "AimFocusDecay", "AimFocusOffsetScale",
        "AimFocusInterval", "VoicePitch"
    ];

    private const BotProfileFields LookFields =
        BotProfileFields.LookAccelNormal | BotProfileFields.LookStiffNormal | BotProfileFields.LookDampNormal
        | BotProfileFields.LookAccelAtk | BotProfileFields.LookStiffAtk | BotProfileFields.LookDampAtk;

    private const BotProfileFields TemplateFields =
        BotProfileFields.Skill | BotProfileFields.ReactionTime | BotProfileFields.AttackDelay
        | BotProfileFields.Aggression | BotProfileFields.Teamwork
        | BotProfileFields.AimFocusInitial | BotProfileFields.AimFocusDecay
        | BotProfileFields.AimFocusOffsetScale | BotProfileFields.AimFocusInterval | BotProfileFields.VoicePitch;

    private readonly Dictionary<string, BotProfilePatch?> _profiles = new(StringComparer.Ordinal);
    // Unknown names and conflicting duplicates must not borrow another bot's personality.
    private readonly BotProfilePatch _defaultLook;

    private BotDifficultyPreset(float[] defaults)
    {
        _defaultLook = CreatePatch(defaults, LookFields);
    }

    public BotProfilePatch GetPatch(string name) =>
        _profiles.GetValueOrDefault(name) ?? _defaultLook;

    public static BotDifficultyPreset Load(TextReader source)
    {
        if (!string.Equals(ReadLine(source), "Default", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Expected a Default block.");

        var defaults = new float[Keys.Length];
        Array.Fill(defaults, float.NaN);
        ReadFields(source, defaults);
        if (Array.Exists(defaults, float.IsNaN))
            throw new FormatException("The Default block must set all difficulty fields.");

        var result = new BotDifficultyPreset(defaults);
        var templates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        while (ReadLine(source) is { } header)
        {
            int separator = header.IndexOfAny([' ', '\t']);
            if (separator < 0) throw new FormatException($"Invalid BotProfile header: {header}");
            string kind = header[..separator];
            string name = header[(separator + 1)..].Trim().Trim('"');
            var values = (float[])defaults.Clone();
            if (kind.Equals("Template", StringComparison.OrdinalIgnoreCase))
            {
                ReadFields(source, values);
                templates[name] = values;
                continue;
            }

            foreach (string template in kind.Split('+'))
            {
                if (!templates.TryGetValue(template, out var inherited))
                    throw new FormatException($"Unknown template {template} for {name}.");

                // CS2 merges non-default template values but does not inherit look-angle fields.
                for (int i = 0; i < inherited.Length; i++)
                    if ((TemplateFields & FieldAt(i)) != 0 && inherited[i] != defaults[i])
                        values[i] = inherited[i];
            }

            ReadFields(source, values);
            var patch = CreatePatch(values, LookFields | TemplateFields);
            if (!result._profiles.TryAdd(name, patch) && result._profiles[name] is { } previous && !previous.Equals(patch))
                result._profiles[name] = null;
        }
        return result;
    }

    private static string? ReadLine(TextReader source)
    {
        while (source.ReadLine() is { } raw)
        {
            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            string line = (comment < 0 ? raw : raw[..comment]).Trim();
            if (line.Length != 0) return line;
        }
        return null;
    }

    private static void ReadFields(TextReader source, float[] values)
    {
        while (ReadLine(source) is { } line)
        {
            if (line.Equals("End", StringComparison.OrdinalIgnoreCase)) return;
            int separator = line.IndexOf('=');
            if (separator < 0) throw new FormatException($"Expected a BotProfile assignment: {line}");
            string key = line[..separator].Trim();
            int index = Array.FindIndex(Keys, field => field.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) values[index] = ParseValue(index, line[(separator + 1)..].Trim());
        }
        throw new FormatException("Missing End in BotProfile.");
    }

    private static BotProfileFields FieldAt(int index) => (BotProfileFields)(1U << index);

    private static float ParseValue(int index, string token)
    {
        var field = FieldAt(index);
        if (field == BotProfileFields.VoicePitch)
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pitch)
                && pitch is >= 0 and <= 255)
                return pitch;

            throw new FormatException($"Invalid {Keys[index]}: {token}");
        }

        bool acceleration = field is BotProfileFields.LookAccelNormal or BotProfileFields.LookAccelAtk;
        // Only the shipped all-nines overflow notation is exempt from normal numeric parsing.
        const string legacySuffix = ".9.9.9.9";
        if (acceleration && token.EndsWith(legacySuffix, StringComparison.Ordinal))
        {
            var digits = token.AsSpan(..^legacySuffix.Length);
            if (digits.Length >= 39 && digits.IndexOfAnyExcept('9') < 0)
                return float.PositiveInfinity;
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            throw new FormatException($"Invalid {Keys[index]}: {token}");

        bool percentage = field is BotProfileFields.Skill or BotProfileFields.Aggression or BotProfileFields.Teamwork;
        if (!(value >= 0) || (!acceleration && !float.IsFinite(value)) || (percentage && value > 100)
            || (field == BotProfileFields.AimFocusDecay && value > 1)
            || (field == BotProfileFields.AimFocusOffsetScale && value >= 1))
            throw new FormatException($"Invalid {Keys[index]}: {token}");
        return percentage ? value / 100f : value;
    }

    private static BotProfilePatch CreatePatch(float[] values, BotProfileFields fields) => new()
    {
        Fields = fields,
        Skill = values[0],
        ReactionTime = values[1],
        AttackDelay = values[2],
        LookAccelNormal = values[3],
        LookStiffNormal = values[4],
        LookDampNormal = values[5],
        LookAccelAtk = values[6],
        LookStiffAtk = values[7],
        LookDampAtk = values[8],
        Aggression = values[9],
        Teamwork = values[10],
        AimFocusInitial = values[11],
        AimFocusDecay = values[12],
        AimFocusOffsetScale = values[13],
        AimFocusInterval = values[14],
        VoicePitch = (int)values[15]
    };
}

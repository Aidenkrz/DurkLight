using System.Text.RegularExpressions;
using Content.Server.Chat.Systems;
using Robust.Shared.Utility;

namespace Content.Server.TTS;

public sealed partial class TTSSystem
{
    [GeneratedRegex(@"[^a-zA-Z0-9,\-+?!. ]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"(?<=[0-9])(\.|,)(?=[0-9])")]
    private static partial Regex DecimalSeparatorRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"\b[\w']+\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordRegex();

    private static readonly Dictionary<string, string> WordReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        {"i'm", "I am"},
        {"i'll", "I will"},
        {"i've", "I have"},
        {"i'd", "I would"},
        {"you're", "you are"},
        {"you'll", "you will"},
        {"you've", "you have"},
        {"you'd", "you would"},
        {"he's", "he is"},
        {"he'll", "he will"},
        {"he'd", "he would"},
        {"she's", "she is"},
        {"she'll", "she will"},
        {"she'd", "she would"},
        {"it's", "it is"},
        {"it'll", "it will"},
        {"it'd", "it would"},
        {"we're", "we are"},
        {"we'll", "we will"},
        {"we've", "we have"},
        {"we'd", "we would"},
        {"they're", "they are"},
        {"they'll", "they will"},
        {"they've", "they have"},
        {"they'd", "they would"},
        {"that's", "that is"},
        {"that'll", "that will"},
        {"that'd", "that would"},
        {"who's", "who is"},
        {"who'll", "who will"},
        {"who'd", "who would"},
        {"what's", "what is"},
        {"what'll", "what will"},
        {"what'd", "what would"},
        {"where's", "where is"},
        {"where'll", "where will"},
        {"where'd", "where would"},
        {"when's", "when is"},
        {"when'll", "when will"},
        {"when'd", "when would"},
        {"why's", "why is"},
        {"why'll", "why will"},
        {"why'd", "why would"},
        {"how's", "how is"},
        {"how'll", "how will"},
        {"how'd", "how would"},
        {"isn't", "is not"},
        {"aren't", "are not"},
        {"wasn't", "was not"},
        {"weren't", "were not"},
        {"haven't", "have not"},
        {"hasn't", "has not"},
        {"hadn't", "had not"},
        {"won't", "will not"},
        {"wouldn't", "would not"},
        {"don't", "do not"},
        {"doesn't", "does not"},
        {"didn't", "did not"},
        {"can't", "cannot"},
        {"couldn't", "could not"},
        {"shouldn't", "should not"},
        {"mightn't", "might not"},
        {"mustn't", "must not"},
        {"let's", "let us"},
        {"there's", "there is"},
        {"here's", "here is"},
        {"ain't", "is not"},

        {"id", "eye dee"},
        {"pda", "P D A"},
        {"ai", "A I"},
        {"nt", "N T"},
        {"rnd", "R and D"},
        {"r&d", "R and D"},
        {"cmo", "C M O"},
        {"ce", "C E"},
        {"hos", "H O S"},
        {"hop", "H O P"},
        {"qm", "Q M"},
        {"rd", "R D"},
        {"atmos", "atmospherics"},
        {"med", "medical"},
        {"sci", "science"},
        {"sec", "security"},
        {"engi", "engineering"},
        {"cargo", "cargo"},
        {"evac", "evacuation"},
        {"maint", "maintenance"},
        {"crit", "critical"},
        {"emag", "E mag"},
        {"nukie", "nuclear operative"},
        {"nukies", "nuclear operatives"},
        {"rev", "revolutionary"},
        {"revs", "revolutionaries"},
        {"ling", "changeling"},
        {"xeno", "xenomorph"},
        {"xenos", "xenomorphs"},
        {"syndie", "syndicate"},
        {"syndies", "syndicates"},
        {"antag", "antagonist"},
        {"antags", "antagonists"},
    };

    private void OnTransformSpeech(TransformSpeechEvent args)
    {
        if (!_isEnabled)
            return;

        args.Message = args.Message.Replace("+", "");
    }

    private static string Sanitize(string text)
    {
        text = text.Trim();
        text = FormattedMessage.RemoveMarkupPermissive(text);
        text = WordRegex().Replace(text, match =>
            WordReplacements.TryGetValue(match.Value, out var replacement) ? replacement : match.Value);
        text = InvalidCharsRegex().Replace(text, "");
        text = DecimalSeparatorRegex().Replace(text, " point ");
        text = DigitsRegex().Replace(text, match => NumberToWords(match.Value));
        return text.Trim();
    }

    private static readonly string[] Ones =
    [
        "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
    ];

    private static readonly string[] Tens =
    [
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
    ];

    private static readonly (long Value, string Name)[] Scales =
    [
        (1_000_000_000_000, "trillion"),
        (1_000_000_000, "billion"),
        (1_000_000, "million"),
        (1_000, "thousand"),
        (100, "hundred")
    ];

    private static string NumberToWords(string digits)
    {
        if (!long.TryParse(digits, out var number))
            return digits;

        return number switch
        {
            0 => "zero",
            < 0 => "negative " + NumberToWords((-number).ToString()),
            >= 1_000_000_000_000_000 => digits,
            _ => ConvertNumber(number)
        };
    }

    private static string ConvertNumber(long number)
    {
        var parts = new List<string>();

        foreach (var (value, name) in Scales)
        {
            if (number < value)
                continue;

            parts.Add($"{NumberToWords((number / value).ToString())} {name}");
            number %= value;
        }

        switch (number)
        {
            case >= 20:
                var ten = Tens[number / 10];
                var one = Ones[number % 10];
                parts.Add(string.IsNullOrEmpty(one) ? ten : $"{ten} {one}");
                break;
            case > 0:
                parts.Add(Ones[number]);
                break;
        }

        return string.Join(" ", parts);
    }
}

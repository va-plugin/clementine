using Clementine.Core;

namespace Clementine.App.Preferences;

/// <summary>Checks the three wheel trigger combinations for problems before they are saved.</summary>
public static class ModifierConflicts
{
    /// <summary>Every problem, as friendly sentences (empty = fine to save).</summary>
    public static IReadOnlyList<string> Find(TriggerModifiers convert, TriggerModifiers tools, TriggerModifiers actions)
    {
        var problems = new List<string>();
        var named = new (string Name, TriggerModifiers Keys)[] { ("Convert", convert), ("Tools", tools), ("Actions", actions) };
        foreach (var (name, keys) in named)
        {
            if (keys == TriggerModifiers.None)
                problems.Add($"The {name} wheel needs at least one key, or every drag would open it.");
        }
        for (var i = 0; i < named.Length; i++)
        {
            for (var j = i + 1; j < named.Length; j++)
            {
                if (named[i].Keys != TriggerModifiers.None && named[i].Keys == named[j].Keys)
                    problems.Add($"{named[i].Name} and {named[j].Name} use the same keys ({KeyText.Describe(named[i].Keys)}).");
            }
        }
        return problems;
    }
}

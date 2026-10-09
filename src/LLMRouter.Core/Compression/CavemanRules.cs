using System.Text.RegularExpressions;

namespace LLMRouter.Core.Compression;

/// <summary>
/// Port of upstream cavemanRules.ts — EN rule table, ordered, with context,
/// category and minIntensity gating (lite=0, full=1, ultra=2).
/// </summary>
public static partial class CavemanRules
{
    public sealed record Rule(
        string Name,
        Regex Pattern,
        string? Replacement,
        Dictionary<string, string>? Map,
        string Context,      // all | user | assistant | system
        string Category,     // filler | context | structural | dedup | terse | ultra
        string MinIntensity, // lite | full | ultra
        string? Description = null)
    {
        public string Apply(Match m)
        {
            if (Map is not null && Map.TryGetValue(m.Value.Trim().ToLowerInvariant(), out var rep)) return rep;
            return Replacement ?? "";
        }
    }

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions IM = I | RegexOptions.Multiline;

    private static Regex Re(string p, RegexOptions o = I) => new(p, o | RegexOptions.Compiled);

    public static readonly Rule[] Rules =
    [
        new("redundant_phrasing",
            Re(@"\b(?:make sure to|be sure to|due to the fact that|the reason is because|it is important to|you should|remember to)\b\s*"),
            null, new() { ["make sure to"] = "ensure ", ["be sure to"] = "ensure ", ["due to the fact that"] = "because ", ["the reason is because"] = "because ", ["it is important to"] = "", ["you should"] = "", ["remember to"] = "" },
            "all", "structural", "full", "Replace verbose stock phrases with shorter equivalents."),
        new("pleasantries",
            Re(@"(?<!make\s)(?<!be\s)\b(?:i'?d be happy to|i would be happy to|i'?d be glad to|i would be glad to|glad to help|happy to|thank you|thanks|no problem|you'?re welcome|absolutely|certainly|of course|sure)\b[,.!?\s]*"),
            "", null, "all", "filler", "lite", "Drop conversational acknowledgements that do not change request meaning."),
        new("polite_framing",
            Re(@"\b(?:please|kindly|could you please|would you please|can you please|i would like you to|i want you to|i need you to)\b\s*"),
            "", null, "all", "filler", "lite"),
        new("hedging",
            Re(@"\b(?:it seems like|it appears that|i think that|i believe that|probably|possibly|maybe it)\b\s*"),
            "", null, "all", "filler", "lite"),
        new("verbose_instructions",
            Re(@"\b(?:provide a detailed explanation of|give me a comprehensive explanation of|write an in-depth explanation of|create a thorough explanation of|provide a detailed|give me a comprehensive|write an in-depth|create a thorough|explain in detail)\b"),
            null, new() { ["provide a detailed explanation of"] = "explain ", ["give me a comprehensive explanation of"] = "explain ", ["write an in-depth explanation of"] = "explain ", ["create a thorough explanation of"] = "explain ", ["provide a detailed"] = "provide ", ["give me a comprehensive"] = "give ", ["write an in-depth"] = "write ", ["create a thorough"] = "create ", ["explain in detail"] = "explain " },
            "all", "filler", "lite"),
        new("filler_adverbs",
            Re(@"(?<![a-z])\b(?:basically|essentially|actually|literally|simply|currently)\b\s*"),
            "", null, "all", "filler", "lite"),
        new("articles",
            Re(@"\b(?:[Aa]n|[Aa]|[Tt]he)\s+(?=[a-z])", RegexOptions.CultureInvariant),
            "", null, "all", "terse", "full", "Remove English articles from prose while protected technical tokens stay intact."),
        new("filler_phrases",
            Re(@"^(?:i want to|i need to|i'd like to|i'm looking for)\b\s*", IM),
            "", null, "user", "filler", "lite"),
        new("redundant_openers",
            Re(@"^(?:hi there|hello|good morning|hey)\s*[,.!?\s]?\s*", IM),
            "", null, "user", "filler", "lite"),
        new("verbose_requests",
            Re(@"\b(?:i was wondering if you could|would it be possible to)\b\s*"),
            "", null, "user", "filler", "lite"),
        new("leader_phrases",
            Re(@"^(?:i'?ll|i will|i can|i'?d|let me|you can|we will|we can|let'?s)\s+(?=[a-z])", IM),
            "", null, "all", "terse", "full", "Remove leading helper phrases before the actual instruction or answer."),
        new("self_reference",
            Re(@"^(?:i am trying to|i am working on|i have been)\b\s*", IM),
            "", null, "user", "filler", "lite"),
        new("excessive_gratitude",
            Re(@"\b(?:thank you so much|thanks in advance|i really appreciate)\b[,.!?\s]*"),
            "", null, "all", "filler", "lite"),
        new("qualifier_removal",
            Re(@"\b(?:a bit|a little|somewhat|kind of|sort of)\b\s*"),
            "", null, "all", "filler", "lite"),
        new("compound_collapse",
            Re(@"\band any potential\b"),
            "", null, "all", "context", "full"),
        new("explanatory_prefix",
            Re(@"\b(?:the function appears to be handling|the code seems to|the class is|this module is)\b"),
            null, new() { ["the function appears to be handling"] = "Function:", ["the code seems to"] = "Code:", ["the class is"] = "Class:", ["this module is"] = "Module:" },
            "all", "context", "lite"),
        new("question_to_directive",
            Re(@"\b(?:can you explain why|could you show me how|would you tell me|can you tell me)\b\s*"),
            null, new() { ["can you explain why"] = "Explain why ", ["could you show me how"] = "Show how ", ["would you tell me"] = "Tell me ", ["can you tell me"] = "Tell me " },
            "user", "context", "lite"),
        new("context_setup",
            Re(@"\b(?:i have the following code|here is my code|below is the code)\b\s*[:.]?\s*"),
            "Code:", null, "user", "context", "lite"),
        new("intent_clarification",
            Re(@"\b(?:what i'm trying to do is|my objective is to|what i need is|i'm aiming to)\b\s*"),
            "Goal:", null, "user", "context", "lite"),
        new("background_removal",
            Re(@"\b(?:as you may know,?\s*|as we discussed earlier,?\s*)"),
            "", null, "all", "context", "lite"),
        new("meta_commentary",
            Re(@"^(?:note that|keep in mind that|remember that)\b\s*", IM),
            "", null, "all", "context", "lite"),
        new("purpose_statement",
            Re(@"\b(?:for the purpose of|with the goal of|in an effort to|for every)\b"),
            null, new() { ["for the purpose of"] = "for", ["with the goal of"] = "to", ["in an effort to"] = "to", ["for every"] = "per" },
            "all", "context", "lite"),
        new("list_conjunction",
            Re(@",\s*and also\s+|,\s*as well as\s+"),
            ", ", null, "all", "structural", "full"),
        new("purpose_phrases",
            Re(@"\b(?:in order to|so as to)\b\s*"),
            "to ", null, "all", "structural", "lite"),
        new("redundant_quantifiers",
            Re(@"\b(?:each and every single|each and every|any and all)\b"),
            null, new() { ["each and every single"] = "each", ["each and every"] = "each", ["any and all"] = "all" },
            "all", "structural", "full"),
        new("verbose_connectors",
            Re(@"\b(?:furthermore|additionally|moreover|in addition)\b\s*"),
            "also ", null, "all", "structural", "lite"),
        new("transition_removal",
            Re(@"^(?:on the other hand,?\s*|in contrast,?\s*|however,?\s*)", IM),
            "", null, "all", "structural", "lite"),
        new("emphasis_removal",
            Re(@"\b(?:very|really|extremely|highly|quite)\s+(?=[a-z])"),
            "", null, "all", "structural", "lite"),
        new("passive_voice",
            Re(@"\b(?:is being used|is being called|is being generated|was created|was generated|was implemented)\b"),
            null, new() { ["is being used"] = "uses", ["is being called"] = "calls", ["is being generated"] = "generated", ["was created"] = "created", ["was generated"] = "generated", ["was implemented"] = "implemented" },
            "all", "structural", "full"),
        new("repeated_context",
            Re(@"\b(?:as we discussed earlier|as mentioned before|as previously stated|as i said before)\b[,.]?\s*"),
            "See above. ", null, "all", "dedup", "lite"),
        new("repeated_question",
            Re(@"\b(?:same question as before|i asked this earlier|this is the same question)\b[,.]?\s*"),
            "[same question] ", null, "user", "dedup", "lite"),
        new("reestablished_context",
            Re(@"\b(?:going back to the code above|referring back to|returning to)\b\s*"),
            "Re: ", null, "all", "dedup", "lite"),
        new("summary_replacement",
            Re(@"\b(?:to summarize what we've discussed|in summary of our conversation|to recap)\b[,.]?\s*"),
            "Summary: ", null, "assistant", "dedup", "lite"),
        new("ultra_abbreviations",
            Re(@"\b(?:database|configuration|function|request|response|implementation|authentication|authorization|application|dependency|dependencies)\b"),
            null, new() { ["database"] = "DB", ["configuration"] = "config", ["function"] = "fn", ["request"] = "req", ["response"] = "res", ["implementation"] = "impl", ["authentication"] = "auth", ["authorization"] = "authz", ["application"] = "app", ["dependency"] = "dep", ["dependencies"] = "deps" },
            "all", "ultra", "ultra"),
    ];

    private static int Rank(string intensity) => intensity switch { "lite" => 0, "ultra" => 2, _ => 1 };

    /// <summary>Rules applicable to a message role at an intensity (same filter as upstream getRulesForContext).</summary>
    public static IEnumerable<Rule> ForContext(string role, string intensity, IReadOnlyCollection<string>? skipRules = null)
    {
        var rank = Rank(intensity);
        return Rules.Where(r => (r.Context == "all" || r.Context == role)
            && Rank(r.MinIntensity) <= rank
            && skipRules?.Contains(r.Name) != true);
    }

    /// <summary>/api/compression/rules + /api/compression/language-packs metadata.</summary>
    public static object[] Metadata() => Rules.Select(r => new
    {
        name = r.Name,
        context = r.Context,
        category = r.Category,
        minIntensity = r.MinIntensity,
        intensities = new[] { "lite", "full", "ultra" }.Where(i => Rank(i) >= Rank(r.MinIntensity)).ToArray(),
        description = r.Description ?? r.Name.Replace('_', ' '),
    }).Cast<object>().ToArray();
}

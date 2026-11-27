namespace Aml.Core.Identity;

/// <summary>
/// The closed English word lists of the dated events read in a Profile's notes for the advisory implausible age
/// (identity-adjudication.md#implausible-age-at-a-dated-document-advisory), kept apart from <see cref="NotesEvents"/> so
/// they can be extended alone. Matched as whole words or phrases, ignoring case. They are known to be incomplete. Every
/// list makes the hint rarer, except <see cref="Acts"/> and <see cref="Pronouns"/>, which make it more frequent.
/// </summary>
internal static class NotesEventWords
{
    /// <summary>The note parts read: the narrative ones (a part before the first label has none). Keywords, People mentioned, Companies mentioned, review and status lines are never read.</summary>
    public static readonly string?[] ReadLabels = [null, "Profile", "Comments"];

    /// <summary>An act of the Profile person: a sentence must hold one for its dates to be events.</summary>
    public static readonly string[] Acts =
    [
        "charged", "indicted", "arrested", "detained", "convicted", "sentenced", "prosecuted", "pleaded guilty", "pled guilty", "found guilty",
        "jailed", "imprisoned", "fined", "wanted", "sanctioned", "extradited",
        "defrauded", "embezzled", "laundered", "bribed", "misappropriated", "smuggled", "trafficked", "forged",
    ];

    /// <summary>The sentence must refer to the Profile person: a token of one of its Latin name forms, or one of these.</summary>
    public static readonly string[] Pronouns = ["he", "she"];

    /// <summary>
    /// Anywhere in the sentence, its subject may be someone else: a relative, an associate or a company (so also "Chen's
    /// company …", "Sample Ltd was fined"). The sentence is skipped, whatever he / she or name it also holds.
    /// </summary>
    public static readonly string[] OtherPersons =
    [
        "company", "companies", "firm", "employer", "colleague", "colleagues", "Ltd", "Inc", "Corp", "LLC", "PLC", "Co.",
        "founded", "established", "incorporated", "registered", "the group", "the corporation", "the entity", "the bank",
        "father", "mother", "parent", "parents", "wife", "husband", "spouse", "son", "sons", "daughter", "daughters", "child", "children",
        "brother", "brothers", "sister", "sisters", "sibling", "siblings", "uncle", "aunt", "cousin", "nephew", "niece",
        "grandfather", "grandmother", "grandparent", "grandson", "granddaughter", "stepfather", "stepmother", "stepson", "stepdaughter", "in-law",
        "girlfriend", "boyfriend", "fiance", "fiancee", "widow", "widower", "family", "relative", "relatives",
        "associate", "associates", "partner", "partners", "accomplice", "accomplices", "co-defendant", "co-defendants",
        "employee", "employees", "aide", "aides", "assistant", "secretary", "bodyguard", "driver",
    ];

    /// <summary>
    /// Before the sentence's act word, another party may be its subject (a victim said he was defrauded); the sentence is
    /// skipped. After it, the word is the act's object (Chen defrauded investors) and the sentence is read.
    /// </summary>
    public static readonly string[] OtherSubjects =
        ["victim", "victims", "witness", "witnesses", "investor", "investors", "informant", "informants", "complainant", "complainants", "prosecutor", "prosecutors", "police"];

    /// <summary>A review, update or listing date, not an event. The sentence is skipped.</summary>
    public static readonly string[] Reviews =
        ["reviewed", "review", "updated", "update", "added to", "listed", "delisted", "as of", "record status", "last checked"];

    /// <summary>Negation or denial: unsure whether the act happened. The sentence is skipped.</summary>
    public static readonly string[] Negations = ["not", "never", "no", "neither", "nor", "denied", "denies", "denying"];

    /// <summary>Hypothesis or future: no act yet. The sentence is skipped. An alleged act (allegedly) still counts.</summary>
    public static readonly string[] Hypotheses =
        ["if", "could", "might", "would", "will", "shall", "likely", "may be", "planned to", "plans to", "planning to", "expected to", "scheduled to", "due to be", "wanted to"];

    /// <summary>A birth: anywhere in the notes the sentence's dates (a bare year too) are birth dates for the guard, never events.</summary>
    public static readonly string[] Births = ["born", "birth", "DOB", "D.O.B."];

    /// <summary>Over this many date mentions in the notes, the notes are silent (no narrative dates a person a hundred times).</summary>
    public const int MaxDates = 100;

    /// <summary>A pattern match that takes longer is abandoned and the notes are silent, as the web readers do.</summary>
    public const int MatchTimeoutMs = 200;
}

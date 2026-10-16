// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Text;

namespace PCButler.Core.Search;

/// <summary>Switches written before a term or a group (case:, path:, file:...).</summary>
[Flags]
public enum QueryModifiers
{
    None = 0,
    /// <summary>case: upper and lower case must match.</summary>
    Case = 1,
    /// <summary>nocase: they need not, whatever the default.</summary>
    NoCase = 2,
    /// <summary>diacritics: accents and Arabic tashkeel must match.</summary>
    Diacritics = 4,
    /// <summary>nodiacritics: they need not.</summary>
    NoDiacritics = 8,
    /// <summary>path: the term is looked for in the whole path, not only the name.</summary>
    Path = 16,
    /// <summary>nopath: only the name, even with a \ in the term.</summary>
    NoPath = 32,
    /// <summary>file: files only.</summary>
    Files = 64,
    /// <summary>folder: folders only.</summary>
    Folders = 128,
}

/// <summary>A search, taken apart.</summary>
public abstract record QueryNode;

/// <summary>Everything (an empty search), narrowed by its modifiers (a lone "file:" is every file).</summary>
public sealed record AllNode(QueryModifiers Modifiers) : QueryNode;

/// <summary>Terms separated by spaces: all must match.</summary>
public sealed record AndNode(IReadOnlyList<QueryNode> Parts) : QueryNode
{
    public bool Equals(AndNode? other) => other is not null && Parts.SequenceEqual(other.Parts);
    public override int GetHashCode() => Parts.Count;
}

/// <summary>Terms separated by |: any may match.</summary>
public sealed record OrNode(IReadOnlyList<QueryNode> Parts) : QueryNode
{
    public bool Equals(OrNode? other) => other is not null && Parts.SequenceEqual(other.Parts);
    public override int GetHashCode() => Parts.Count;
}

/// <summary>!term: must not match.</summary>
public sealed record NotNode(QueryNode Part) : QueryNode;

/// <summary>Text to find: in the name, or in the path when it holds a \ (or with path:); with * or ? it must match the whole name.</summary>
public sealed record TermNode(string Text, QueryModifiers Modifiers) : QueryNode;

/// <summary>ext:jpg;png: files with one of these extensions ("" = no extension).</summary>
public sealed record ExtNode(IReadOnlyList<string> Extensions, QueryModifiers Modifiers) : QueryNode
{
    public bool Equals(ExtNode? other) => other is not null && Modifiers == other.Modifiers && Extensions.SequenceEqual(other.Extensions);
    public override int GetHashCode() => Extensions.Count;
}

/// <summary>
/// Turns what is typed in the search box into a QueryNode tree. The syntax is the one users of
/// other file search tools know:
///   space = AND, | = OR (it binds tighter than the space), ! = NOT, &lt; &gt; group,
///   "..." keeps spaces and operators as plain text, * and ? are wildcards,
///   a \ in a term matches the path, and the modifiers case:, nocase:, diacritics:,
///   nodiacritics:, path:, nopath:, file:, folder: and the function ext:list.
/// A word with a colon that is not one of these (C:\Users) is plain text.
/// No Windows APIs here: the phone app can parse searches too.
/// </summary>
public static class QueryParser
{
    private static readonly Dictionary<string, QueryModifiers> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["case"] = QueryModifiers.Case, ["nocase"] = QueryModifiers.NoCase,
        ["diacritics"] = QueryModifiers.Diacritics, ["nodiacritics"] = QueryModifiers.NoDiacritics,
        ["path"] = QueryModifiers.Path, ["nopath"] = QueryModifiers.NoPath,
        ["file"] = QueryModifiers.Files, ["files"] = QueryModifiers.Files,
        ["folder"] = QueryModifiers.Folders, ["folders"] = QueryModifiers.Folders,
    };

    private enum Kind { Word, Or, Not, Open, Close, Modifier, Ext }

    /// <summary>One piece of the text. Attached = the next piece follows with no space (a modifier applies to it).</summary>
    private sealed record Token(Kind Kind, string Text, QueryModifiers Modifier = QueryModifiers.None, bool Attached = false);

    public static QueryNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var node = Sequence(tokens, ref position, QueryModifiers.None, topLevel: true);
        return node;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }
            switch (c)
            {
                case '|':
                    tokens.Add(new Token(Kind.Or, "|"));
                    index++;
                    continue;
                case '<':
                    tokens.Add(new Token(Kind.Open, "<"));
                    index++;
                    continue;
                case '>':
                    tokens.Add(new Token(Kind.Close, ">"));
                    index++;
                    continue;
                case '!':
                    tokens.Add(new Token(Kind.Not, "!"));
                    index++;
                    continue;
            }
            // A modifier: a known name and a colon, written before any quote.
            var colon = text.IndexOf(':', index);
            if (colon > index && Word(text, index, colon) is var name && name.Length == colon - index
                && (Modifiers.TryGetValue(name, out var modifier) || name.Equals("ext", StringComparison.OrdinalIgnoreCase)))
            {
                index = colon + 1;
                var attached = index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('|' or '>');
                if (name.Equals("ext", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ReadWord(text, ref index);
                    tokens.Add(new Token(Kind.Ext, value));
                }
                else
                    tokens.Add(new Token(Kind.Modifier, name, modifier, attached));
                continue;
            }
            tokens.Add(new Token(Kind.Word, ReadWord(text, ref index)));
        }
        return tokens;
    }

    /// <summary>The plain letters from start up to end, stopping at the first character a modifier name can't have.</summary>
    private static string Word(string text, int start, int end)
    {
        var length = 0;
        while (start + length < end && char.IsAsciiLetter(text[start + length]))
            length++;
        return text.Substring(start, length);
    }

    /// <summary>A word up to a space or an operator; quoted parts keep spaces and operators.</summary>
    private static string ReadWord(string text, ref int index)
    {
        var word = new StringBuilder();
        var quoted = false;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '"')
            {
                quoted = !quoted;
                index++;
                continue;
            }
            if (!quoted && (char.IsWhiteSpace(c) || c is '|' or '<' or '>'))
                break;
            word.Append(c);
            index++;
        }
        return word.ToString();
    }

    /// <summary>Terms one after another (AND), up to a closing &gt; or the end.</summary>
    private static QueryNode Sequence(List<Token> tokens, ref int position, QueryModifiers inherited, bool topLevel)
    {
        var parts = new List<QueryNode>();
        while (position < tokens.Count)
        {
            if (tokens[position].Kind == Kind.Close)
            {
                position++;
                if (topLevel)
                    continue; // a stray > is ignored
                break;
            }
            if (tokens[position].Kind == Kind.Or)
            {
                position++; // an | with nothing before it
                continue;
            }
            var part = Alternatives(tokens, ref position, inherited);
            // An empty group or a lone operator adds nothing to an AND.
            if (part is not AllNode { Modifiers: QueryModifiers.None } || parts.Count == 0)
                parts.Add(part);
        }
        if (parts.Count > 1)
            parts.RemoveAll(part => part is AllNode { Modifiers: QueryModifiers.None });
        return parts.Count switch
        {
            0 => new AllNode(inherited),
            1 => parts[0],
            _ => new AndNode(parts),
        };
    }

    /// <summary>Terms joined by |.</summary>
    private static QueryNode Alternatives(List<Token> tokens, ref int position, QueryModifiers inherited)
    {
        var parts = new List<QueryNode> { Unary(tokens, ref position, inherited) };
        while (position < tokens.Count && tokens[position].Kind == Kind.Or)
        {
            position++;
            if (position >= tokens.Count || tokens[position].Kind is Kind.Close or Kind.Or)
                continue;
            parts.Add(Unary(tokens, ref position, inherited));
        }
        return parts.Count == 1 ? parts[0] : new OrNode(parts);
    }

    private static QueryNode Unary(List<Token> tokens, ref int position, QueryModifiers inherited)
    {
        if (position >= tokens.Count)
            return new AllNode(inherited);
        var token = tokens[position++];
        switch (token.Kind)
        {
            case Kind.Not:
                return position < tokens.Count && tokens[position].Kind is not (Kind.Close or Kind.Or)
                    ? new NotNode(Unary(tokens, ref position, inherited))
                    : new AllNode(inherited);
            case Kind.Open:
                return Sequence(tokens, ref position, inherited, topLevel: false);
            case Kind.Ext:
                return new ExtNode(token.Text.Split(';', ',').Select(part => part.Trim().TrimStart('.')).Distinct().ToList(), inherited);
            case Kind.Modifier:
                var combined = Combine(inherited, token.Modifier);
                // "file:" on its own is every file; a modifier with something right after it applies to that.
                return token.Attached && position < tokens.Count && tokens[position].Kind is not (Kind.Close or Kind.Or)
                    ? Unary(tokens, ref position, combined)
                    : new AllNode(combined);
            default:
                return token.Text.Length == 0 ? new AllNode(inherited) : new TermNode(token.Text, inherited);
        }
    }

    /// <summary>A modifier written inside a group overrides its opposite from outside.</summary>
    private static QueryModifiers Combine(QueryModifiers outer, QueryModifiers added)
    {
        var opposite = added switch
        {
            QueryModifiers.Case => QueryModifiers.NoCase,
            QueryModifiers.NoCase => QueryModifiers.Case,
            QueryModifiers.Diacritics => QueryModifiers.NoDiacritics,
            QueryModifiers.NoDiacritics => QueryModifiers.Diacritics,
            QueryModifiers.Path => QueryModifiers.NoPath,
            QueryModifiers.NoPath => QueryModifiers.Path,
            QueryModifiers.Files => QueryModifiers.Folders,
            QueryModifiers.Folders => QueryModifiers.Files,
            _ => QueryModifiers.None,
        };
        return (outer & ~opposite) | added;
    }
}

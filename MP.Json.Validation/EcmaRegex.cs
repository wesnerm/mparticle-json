// Copyright (c) 2020, mParticle, Inc
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//   https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MP.Json.Validation
{
    /// <summary>
    /// Translates JSON Schema regular expressions (ECMA-262 syntax, with Unicode-mode
    /// semantics for '.' and astral characters) into equivalent .NET regular expressions.
    /// </summary>
    /// <remarks>
    /// Differences handled:
    /// - \d \D \w \W \s \S use the ECMA-262 character sets (ASCII digits and word
    ///   characters; ECMA-262 whitespace and line terminators), not .NET's Unicode sets.
    /// - \b \B use ECMA-262 (ASCII) word characters.
    /// - $ matches only at the end of the input, not before a final newline.
    /// - . matches any code point except line terminators (\n \r U+2028 U+2029);
    ///   a surrogate pair is one code point.
    /// - A literal astral character (surrogate pair) outside a class is one atom for quantifiers.
    /// - [] matches nothing and [^] matches anything; '[' inside a class is a literal.
    /// - .NET-only syntax is rejected: \A \Z \z \G anchors, inline options (?i),
    ///   atomic groups (?>, conditionals (?(, comments (?#, (?'name') groups and
    ///   balancing groups.
    /// Unrecognized escapes are passed to .NET unchanged.
    /// </remarks>
    public static class EcmaRegex
    {
        private const RegexOptions Options = RegexOptions.CultureInvariant;

        private static readonly (int lo, int hi)[] Digit = { (0x30, 0x39) };
        private static readonly (int lo, int hi)[] Word = { (0x30, 0x39), (0x41, 0x5A), (0x5F, 0x5F), (0x61, 0x7A) };
        private static readonly (int lo, int hi)[] Space =
        {
            (0x09, 0x0D), (0x20, 0x20), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A),
            (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000), (0xFEFF, 0xFEFF),
        };

        private const string WordClass = "[0-9A-Za-z_]";
        private const string WordBoundary = "(?:(?<=" + WordClass + ")(?!" + WordClass + ")|(?<!" + WordClass + ")(?=" + WordClass + "))";
        private const string NonWordBoundary = "(?:(?<=" + WordClass + ")(?=" + WordClass + ")|(?<!" + WordClass + ")(?!" + WordClass + "))";
        // Any code point except line terminators. A surrogate pair is matched whole and is
        // never split by backtracking; an unpaired surrogate counts as one code point.
        private const string AnyCodePoint =
            "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]" +
            "|[\\uD800-\\uDBFF](?![\\uDC00-\\uDFFF])" +
            "|(?<![\\uD800-\\uDBFF])[\\uDC00-\\uDFFF]" +
            "|[^\\n\\r\\u2028\\u2029\\uD800-\\uDFFF])";

        /// <summary>
        /// Builds a .NET regex with ECMA-262 semantics, or returns null if the pattern
        /// is not a valid ECMA-262 regular expression.
        /// </summary>
        public static Regex Create(string pattern, TimeSpan matchTimeout)
        {
            if (!TryTranslate(pattern, out string translated))
                return null;
            try
            {
                return new Regex(translated, Options, matchTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the pattern is a valid ECMA-262 regular expression (as far as .NET can tell).
        /// </summary>
        public static bool IsValid(string pattern) => Create(pattern, Regex.InfiniteMatchTimeout) != null;

        /// <summary>
        /// Translates an ECMA-262 pattern into a .NET pattern with the same meaning.
        /// </summary>
        public static bool TryTranslate(string pattern, out string translated)
        {
            translated = null;
            if (pattern == null)
                return false;

            var sb = new StringBuilder(pattern.Length + 16);
            bool inClass = false;

            for (int i = 0; i < pattern.Length; i++)
            {
                char ch = pattern[i];

                if (ch == '\\')
                {
                    if (++i >= pattern.Length)
                        return false; // trailing backslash
                    if (!TranslateEscape(pattern, ref i, inClass, sb))
                        return false;
                    continue;
                }

                if (inClass)
                {
                    if (ch == ']')
                        inClass = false;
                    else if (ch == '[')
                    {
                        sb.Append("\\[");  // literal in ECMA-262; avoids .NET class subtraction
                        continue;
                    }
                    sb.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '[':
                        int start = i + 1;
                        bool negated = start < pattern.Length && pattern[start] == '^';
                        int contentStart = negated ? start + 1 : start;
                        if (contentStart < pattern.Length && pattern[contentStart] == ']')
                        {
                            // [] matches nothing; [^] matches any character
                            sb.Append(negated ? "[\\s\\S]" : "(?!)");
                            i = contentStart;
                            continue;
                        }
                        inClass = true;
                        sb.Append(negated ? "[^" : "[");
                        i = negated ? start : i;
                        continue;

                    case '.':
                        sb.Append(AnyCodePoint);
                        continue;

                    case '$':
                        sb.Append("\\z");
                        continue;

                    case '(':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                        {
                            int len = GroupPrefixLength(pattern, i);
                            if (len == 0)
                                return false; // .NET-only group syntax
                            sb.Append(pattern, i, len);
                            i += len - 1;
                            continue;
                        }
                        sb.Append(ch);
                        continue;

                    default:
                        if (char.IsHighSurrogate(ch) && i + 1 < pattern.Length && char.IsLowSurrogate(pattern[i + 1]))
                        {
                            // An astral character is one atom, so a following quantifier applies to all of it
                            sb.Append("(?:").Append(ch).Append(pattern[i + 1]).Append(')');
                            i++;
                            continue;
                        }
                        sb.Append(ch);
                        continue;
                }
            }

            if (inClass)
                return false; // unterminated class

            translated = sb.ToString();
            return true;
        }

        private static bool TranslateEscape(string pattern, ref int i, bool inClass, StringBuilder sb)
        {
            char e = pattern[i];
            switch (e)
            {
                case 'd': AppendSet(sb, Digit, false, inClass); return true;
                case 'D': AppendSet(sb, Digit, true, inClass); return true;
                case 'w': AppendSet(sb, Word, false, inClass); return true;
                case 'W': AppendSet(sb, Word, true, inClass); return true;
                case 's': AppendSet(sb, Space, false, inClass); return true;
                case 'S': AppendSet(sb, Space, true, inClass); return true;

                case 'b':
                    // Inside a class, \b is a backspace in both dialects
                    sb.Append(inClass ? "\\b" : WordBoundary);
                    return true;

                case 'B':
                    if (inClass)
                        return false;
                    sb.Append(NonWordBoundary);
                    return true;

                case 'A':
                case 'Z':
                case 'z':
                case 'G':
                    return false; // .NET-only anchors

                case 'u':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '{')
                    {
                        // \u{XXXXX} code point escape
                        int close = pattern.IndexOf('}', i + 2);
                        if (close < 0 || !int.TryParse(pattern.AsSpan(i + 2, close - i - 2), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out int cp) || cp > 0x10FFFF)
                            return false;
                        i = close;
                        if (cp > 0xFFFF)
                        {
                            string pair = char.ConvertFromUtf32(cp);
                            if (inClass)
                                return false; // astral characters in a class are not supported
                            sb.Append("(?:").Append(Hex(pair[0])).Append(Hex(pair[1])).Append(')');
                        }
                        else
                        {
                            sb.Append(Hex(cp));
                        }
                        return true;
                    }
                    sb.Append('\\').Append(e);
                    return true;

                default:
                    // \uXXXX \xHH \cX \k<name> \p{..} \n \t \/ \. backreferences etc.
                    // have the same meaning in .NET; pass them through.
                    sb.Append('\\').Append(e);
                    return true;
            }
        }

        /// <summary>
        /// Length of an allowed "(?..." group prefix at <paramref name="i"/>, or 0 if
        /// the group uses syntax that ECMA-262 does not have.
        /// </summary>
        private static int GroupPrefixLength(string pattern, int i)
        {
            int j = i + 2;
            if (j >= pattern.Length)
                return 0;

            switch (pattern[j])
            {
                case ':':
                case '=':
                case '!':
                    return 3;
                case '<':
                    if (j + 1 < pattern.Length && (pattern[j + 1] == '=' || pattern[j + 1] == '!'))
                        return 4; // lookbehind
                    // Named group: (?<name>
                    int k = j + 1;
                    if (k >= pattern.Length || !(char.IsLetter(pattern[k]) || pattern[k] == '_'))
                        return 0;
                    while (k < pattern.Length && (char.IsLetterOrDigit(pattern[k]) || pattern[k] == '_'))
                        k++;
                    return k < pattern.Length && pattern[k] == '>' ? k - i + 1 : 0;
                default:
                    return 0; // (?i) (?> (?( (?# (?'name' ...
            }
        }

        private static void AppendSet(StringBuilder sb, (int lo, int hi)[] ranges, bool negate, bool inClass)
        {
            if (inClass)
            {
                // Inside a class, a negated set must be spelled out as its complement
                AppendRanges(sb, negate ? Complement(ranges) : ranges);
                return;
            }

            sb.Append(negate ? "[^" : "[");
            AppendRanges(sb, ranges);
            sb.Append(']');
        }

        private static void AppendRanges(StringBuilder sb, (int lo, int hi)[] ranges)
        {
            foreach (var (lo, hi) in ranges)
            {
                sb.Append(Hex(lo));
                if (hi != lo)
                    sb.Append('-').Append(Hex(hi));
            }
        }

        private static (int lo, int hi)[] Complement((int lo, int hi)[] ranges)
        {
            var result = new System.Collections.Generic.List<(int, int)>();
            int next = 0;
            foreach (var (lo, hi) in ranges)
            {
                if (lo > next)
                    result.Add((next, lo - 1));
                next = hi + 1;
            }
            if (next <= 0xFFFF)
                result.Add((next, 0xFFFF));
            return result.ToArray();
        }

        private static string Hex(int ch) => "\\u" + ch.ToString("X4", CultureInfo.InvariantCulture);
    }
}

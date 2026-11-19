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
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MP.Json.Validation
{
    /// <summary>
    /// Standard format regexes, kept in a separate class to prevent initialization if not used.
    /// </summary>

    public class FormatRegexes
    {
        private const RegexOptions StandardOptions =
            0 // RegexOptions.Compiled
            | RegexOptions.CultureInvariant
            | RegexOptions.ExplicitCapture
            | RegexOptions.IgnoreCase
            | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.Singleline;

        // These are .NET regexes. They use [0-9] rather than \d (which matches any Unicode
        // digit) and end with \z rather than $ (which also matches before a final newline).

        public static readonly Regex Email = new Regex(@"^([A-Z0-9+.\-])+\@([A-Z0-9\-.])+\.[A-Z0-9\-]+\z", StandardOptions);

        // Labels are 1-63 letters, digits and hyphens, and do not start or end with a hyphen
        public static readonly Regex Hostname = new Regex(@"^([A-Z0-9]([A-Z0-9-]{0,61}[A-Z0-9])?\.)*[A-Z0-9]([A-Z0-9-]{0,61}[A-Z0-9])?\z", StandardOptions);

        // Dotted decimal without leading zeros (which some parsers read as octal)
        public static readonly Regex Ipv4 = new Regex(@"^((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z", StandardOptions);

        public static readonly Regex Uuid = new Regex(@"^[0-9A-F]{8}\-[0-9A-F]{4}\-[0-9A-F]{4}\-[0-9A-F]{4}\-[0-9A-F]{12}\z", StandardOptions);

        public static readonly Regex UriReference = new Regex(@"^(([^:/?#]+):)?(//([^/?#]*))?([^?#]*)(\?([^#]*))?(\#(.*))?", StandardOptions);

        public static readonly Regex Duration = new Regex(@"^(-?)P(?=[0-9]|T[0-9])(([0-9]+)Y)?(([0-9]+)M)?(([0-9]+)([DW]))?(T(([0-9]+)H)?(([0-9]+)M)?(([0-9]+(\.[0-9]+)?)S)?)?\z", StandardOptions);

        public static readonly Regex JsonPointer = new Regex(@"^(/([^~]|~[01])*)?\z", StandardOptions);

        // A non-negative integer without leading zeros, then "#" or a JSON pointer
        public static readonly Regex RelativeJsonPointer = new Regex(@"^(0|[1-9][0-9]*)(\#|/([^#~]|~[01])*)?\z", StandardOptions);
    }
}

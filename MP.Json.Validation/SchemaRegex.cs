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
    public class SchemaRegex
    {
        /// <summary>
        /// Per-match timeout. .NET measures regex timeouts with Environment.TickCount64,
        /// which advances in ~15.6ms steps on Windows, so any limit below that can fire on
        /// matches that take microseconds. 100ms stays well clear of the clock granularity
        /// while still bounding catastrophic backtracking.
        /// </summary>
        private const double TimeLimitMs = 100;

        public SchemaRegex(string pattern, RegexType type = RegexType.Normal)
        {
            Pattern = pattern;
            Type = type;

            if (type == RegexType.Normal)
            {
                // JSON Schema patterns use ECMA-262 semantics, not .NET's
                RegularExpression = EcmaRegex.Create(pattern, TimeSpan.FromMilliseconds(TimeLimitMs));
                if (RegularExpression == null)
                    Type = RegexType.FaultedInvalid;
            }
        }

        public string Pattern { get; }

        public Regex RegularExpression { get; private set; }

        public RegexType Type { get; private set; }

        public int CallCount { get; private set; }


        public SchemaRegexSuccess Matches(string value)
        {
            bool success = true;
            CallCount++;

            switch(Type)
            {
                case RegexType.Normal:
                    return MatchNormally(value);

                case RegexType.DateTime: 
                    success = Formats.IsDateTime(value);
                    break;

                case RegexType.Date: 
                    success = Formats.IsDate(value);
                    break;

                case RegexType.Number:
                    success = Formats.IsNumber(value);
                    break;

                case RegexType.Boolean:
                    success = Formats.IsBoolean(value);
                    break;

                case RegexType.FaultedTimedOut:
                    return SchemaRegexSuccess.TimedOut;

                case RegexType.FaultedException:
                    return SchemaRegexSuccess.Exception;

                case RegexType.FaultedInvalid:
                    return SchemaRegexSuccess.Failed;
            }

            return success ? SchemaRegexSuccess.Success : SchemaRegexSuccess.Failed;
        }

        /// <remarks>
        /// Failures only affect the current match. SchemaRegex instances are cached and
        /// shared process-wide (see SchemaRegexCache), so faulting the instance after one
        /// timeout or exception would silently break the pattern for every schema and
        /// every later document.
        /// </remarks>
        private SchemaRegexSuccess MatchNormally(string value)
        {
            try
            {
                bool result = RegularExpression.IsMatch(value);
                return result
                    ? SchemaRegexSuccess.Success
                    : SchemaRegexSuccess.Failed;
            }
            catch (RegexMatchTimeoutException)
            {
                return SchemaRegexSuccess.TimedOut;
            }
            catch (ArgumentException)
            {
                return SchemaRegexSuccess.Exception;
            }
        }

        public enum RegexType : byte
        {
            Normal,
            Date,
            DateTime,
            Number,
            Boolean,

            FaultedInvalid,
            FaultedTimedOut,
            FaultedException,
        }


        public override string ToString()
        {
            return Pattern;
        }

    }
}

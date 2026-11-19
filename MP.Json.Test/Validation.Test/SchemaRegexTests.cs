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

using System.Collections.Generic;
using MP.Json.Validation;
using Xunit;

namespace MP.Json.Test
{
    public class SchemaRegexTests
    {
        // Exponential backtracking on a non-matching suffix; reliably exceeds the 100ms limit.
        private static readonly string Catastrophic = new string('a', 60) + "!";

        [Fact]
        public void TimeoutDoesNotFaultTheRegex()
        {
            var regex = new SchemaRegex("^(a|aa)+$");

            Assert.Equal(SchemaRegexSuccess.TimedOut, regex.Matches(Catastrophic));

            // A timeout only affects that match; the regex keeps working afterwards.
            Assert.Equal(SchemaRegex.RegexType.Normal, regex.Type);
            Assert.Equal(SchemaRegexSuccess.Success, regex.Matches("aaaa"));
            Assert.Equal(SchemaRegexSuccess.Failed, regex.Matches("aab"));
        }

        [Fact]
        public void NullValueDoesNotFaultTheRegex()
        {
            var regex = new SchemaRegex("^x$");

            Assert.Equal(SchemaRegexSuccess.Exception, regex.Matches(null));
            Assert.Equal(SchemaRegexSuccess.Success, regex.Matches("x"));
        }

        [Fact]
        public void SchemaStillValidatesAfterTimeout()
        {
            // SchemaRegex instances are cached process-wide, so a timeout in one
            // validation must not break later validations against the same pattern.
            MPSchema schema = MPJson.Parse("{ 'type': 'string', 'pattern': '^(a|a?)+$' }");

            var errors = new List<ErrorType>();
            var validator = new JsonValidator();
            Assert.False(validator.Validate(schema, MPJson.Parse($"'{Catastrophic}'"),
                (sender, e) => errors.Add(e.ErrorType)));
            Assert.Contains(ErrorType.TimeLimit, errors);

            Assert.True(new JsonValidator().Validate(schema, MPJson.Parse("'aaaa'")));
            Assert.False(new JsonValidator().Validate(schema, MPJson.Parse("'aab'")));
        }
    }
}

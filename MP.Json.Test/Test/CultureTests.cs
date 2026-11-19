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
using MP.Json.Validation;
using Xunit;

namespace MP.Json.Test
{
    /// <summary>
    /// JSON numbers always use '.' as the decimal separator, so parsing and
    /// formatting must not depend on the current culture.
    /// </summary>
    public class CultureTests
    {
        // Cultures that use ',' as the decimal separator (and '.' or a space for grouping)
        public static readonly TheoryData<string> Cultures = new TheoryData<string> { "de-DE", "fr-FR", "ru-RU" };

        private static void WithCulture(string name, Action action)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public void ParsesNumbersInvariantly(string culture)
        {
            WithCulture(culture, () =>
            {
                Assert.Equal(1.5, (double)MPJson.Parse("1.5"));
                Assert.Equal(-0.25, (double)MPJson.Parse("-0.25"));
                Assert.Equal(1.5e10, (double)MPJson.Parse("1.5e10"));
                Assert.Equal(1234.5, (double)MPJson.Parse("[ 1234.5 ]")[0]);
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public void FormatsNumbersInvariantly(string culture)
        {
            WithCulture(culture, () =>
            {
                Assert.Equal("1.5", new MPJson(1.5).ToString());
                Assert.Equal("[ 1234.5, -0.25 ]", MPJson.Parse("[ 1234.5, -0.25 ]").ToString());
                Assert.Equal("1E+300", new MPJson(1e300).ToString());

                // Output must round-trip through the parser
                var json = MPJson.Parse("{ \"a\": 3.14159, \"b\": [ 0.5 ] }");
                Assert.Equal(json, MPJson.Parse(json.ToString()));
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public void ValidatesDecimalLimits(string culture)
        {
            WithCulture(culture, () =>
            {
                var schema = new MPSchema(MPJson.Parse("{ \"maximum\": 2.5, \"multipleOf\": 0.5 }"));
                Assert.True(schema.Validate(MPJson.Parse("2.5")));
                Assert.False(schema.Validate(MPJson.Parse("2.75")));
                Assert.False(schema.Validate(MPJson.Parse("3")));
            });
        }
    }
}

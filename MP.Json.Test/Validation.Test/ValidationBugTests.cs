﻿// Copyright (c) 2020, mParticle, Inc
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
    /// <summary>
    /// Regression tests for validation bugs found by running the full JSON Schema
    /// Test Suite plus targeted cases.
    /// </summary>
    public class ValidationBugTests
    {
        private static MPSchema Schema(string schema)
        {
            var result = new MPSchema(MPJson.Parse(schema));
            Assert.True(result.IsValid, "schema should be valid: " + schema);
            return result;
        }

        private static bool IsValid(string schema, string data, bool strict = false)
            => new JsonValidator { Strict = strict }.Validate(Schema(schema), MPJson.Parse(data));

        #region Bounds of zero

        [Theory]
        [InlineData("{ 'minItems': 0 }", "[]", true)]
        [InlineData("{ 'maxItems': 0 }", "[]", true)]
        [InlineData("{ 'maxItems': 0 }", "[ 1 ]", false)]
        [InlineData("{ 'minLength': 0 }", "''", true)]
        [InlineData("{ 'maxLength': 0 }", "''", true)]
        [InlineData("{ 'maxLength': 0 }", "'a'", false)]
        [InlineData("{ 'minProperties': 0 }", "{}", true)]
        [InlineData("{ 'maxProperties': 0 }", "{}", true)]
        [InlineData("{ 'maxProperties': 0 }", "{ 'a': 1 }", false)]
        [InlineData("{ 'contains': { 'const': 1 }, 'minContains': 0 }", "[ 2 ]", true)]
        [InlineData("{ 'contains': { 'const': 1 }, 'maxContains': 0 }", "[ 1 ]", false)]
        [InlineData("{ 'properties': { 'tags': { 'type': 'array', 'minItems': 0 } } }", "{ 'tags': [] }", true)]
        public void ZeroBoundIsAllowed(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Theory]
        [InlineData("{ 'minItems': -1 }")]
        [InlineData("{ 'maxLength': 1.5 }")]
        [InlineData("{ 'minProperties': '1' }")]
        public void InvalidBoundRejectsSchema(string schema)
        {
            Assert.False(new MPSchema(MPJson.Parse(schema)).IsValid);
        }

        #endregion

        #region String length in code points

        [Theory]
        [InlineData("{ 'maxLength': 1 }", "'\\ud83d\\udca9'", true)]       // one astral code point
        [InlineData("{ 'minLength': 2 }", "'\\ud83d\\udca9'", false)]
        [InlineData("{ 'maxLength': 2 }", "'\\ud83d\\udca9\\ud83d\\udca9'", true)]
        [InlineData("{ 'maxLength': 3 }", "'a\\ud83d\\udca9b'", true)]
        [InlineData("{ 'maxLength': 2 }", "'a\\ud83d\\udca9b'", false)]
        [InlineData("{ 'maxLength': 1 }", "'e\\u0301'", false)]             // two code points, one grapheme
        [InlineData("{ 'minLength': 2 }", "'e\\u0301'", true)]
        [InlineData("{ 'maxLength': 1 }", "'\\ud83d'", true)]               // unpaired surrogate counts as one
        [InlineData("{ 'minLength': 2 }", "'\\udca9\\ud83d'", true)]        // reversed pair is two
        [InlineData("{ 'minLength': 3, 'maxLength': 3 }", "'abc'", true)]
        public void LengthCountsCodePoints(string schema, string data, bool expected)
        {
            // Same result whether or not Strict is set
            Assert.Equal(expected, IsValid(schema, data, strict: false));
            Assert.Equal(expected, IsValid(schema, data, strict: true));
        }

        #endregion

        #region $ref cycle detection

        [Theory]
        // Composition keywords nested around a $ref near the root
        [InlineData("{ 'allOf': [ { 'anyOf': [ { '$ref': '#/definitions/a' } ] } ], 'definitions': { 'a': { 'type': 'integer' } } }", "1", true)]
        [InlineData("{ 'allOf': [ { 'anyOf': [ { '$ref': '#/definitions/a' } ] } ], 'definitions': { 'a': { 'type': 'integer' } } }", "'x'", false)]
        [InlineData("{ 'oneOf': [ { 'allOf': [ { 'not': { 'not': { '$ref': '#/definitions/a' } } } ] } ], 'definitions': { 'a': { 'type': 'integer' } } }", "1", true)]
        // A long chain of references that never descends into the instance
        [InlineData("{ '$ref': '#/definitions/a', 'definitions': { 'a': { '$ref': '#/definitions/b' }, 'b': { '$ref': '#/definitions/c' }, 'c': { '$ref': '#/definitions/d' }, 'd': { '$ref': '#/definitions/e' }, 'e': { 'allOf': [ { '$ref': '#/definitions/f' } ] }, 'f': { 'type': 'integer' } } }", "1", true)]
        [InlineData("{ '$ref': '#/definitions/a', 'definitions': { 'a': { '$ref': '#/definitions/b' }, 'b': { '$ref': '#/definitions/c' }, 'c': { '$ref': '#/definitions/d' }, 'd': { '$ref': '#/definitions/e' }, 'e': { 'allOf': [ { '$ref': '#/definitions/f' } ] }, 'f': { 'type': 'integer' } } }", "'x'", false)]
        // The same $ref used twice side by side is not a cycle
        [InlineData("{ 'allOf': [ { '$ref': '#/definitions/a' }, { '$ref': '#/definitions/a' } ], 'definitions': { 'a': { 'type': 'integer' } } }", "1", true)]
        public void NestedRefsAreNotLimitedByDepth(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        private const string TreeSchema =
            "{ '$ref': '#/definitions/node', 'definitions': { 'node': { 'type': 'object', 'required': [ 'value' ], " +
            "'properties': { 'value': { 'type': 'integer' }, 'children': { 'type': 'array', 'items': { '$ref': '#/definitions/node' } } } } } }";

        [Theory]
        [InlineData("{ 'value': 1, 'children': [ { 'value': 2, 'children': [ { 'value': 3, 'children': [ { 'value': 4 } ] } ] } ] }", true)]
        [InlineData("{ 'value': 1, 'children': [ { 'value': 2, 'children': [ { 'value': 3, 'children': [ { 'value': 'x' } ] } ] } ] }", false)]
        public void RecursiveSchemaThatDescendsWorks(string data, bool expected)
        {
            Assert.Equal(expected, IsValid(TreeSchema, data));
        }

        [Theory]
        [InlineData("{ '$ref': '#' }")]
        [InlineData("{ '$ref': '#/definitions/a', 'definitions': { 'a': { '$ref': '#/definitions/b' }, 'b': { '$ref': '#/definitions/a' } } }")]
        [InlineData("{ '$ref': '#/definitions/a', 'definitions': { 'a': { 'allOf': [ { '$ref': '#/definitions/a' } ] } } }")]
        public void RefCycleWithoutProgressFails(string schema)
        {
            // An infinite loop must fail cleanly (no stack overflow) and report a $ref error
            var errors = new List<ErrorType>();
            bool valid = new JsonValidator().Validate(Schema(schema), MPJson.Parse("1"), (s, e) => errors.Add(e.ErrorType));

            Assert.False(valid);
            Assert.Contains(ErrorType.Ref, errors);
        }

        [Fact]
        public void ValidatorCanBeReusedAfterCycle()
        {
            var validator = new JsonValidator();
            Assert.False(validator.Validate(Schema("{ '$ref': '#' }"), MPJson.Parse("1")));
            Assert.True(validator.Validate(Schema(TreeSchema), MPJson.Parse("{ 'value': 1 }")));
        }

        #endregion

        #region $schema detection

        [Theory]
        [InlineData("http://json-schema.org/draft-04/schema#", SchemaVersion.Draft4)]
        [InlineData("http://json-schema.org/draft-04/schema", SchemaVersion.Draft4)]
        [InlineData("http://json-schema.org/draft-06/schema#", SchemaVersion.Draft6)]
        [InlineData("http://json-schema.org/draft-07/schema#", SchemaVersion.Draft7)]
        [InlineData("https://json-schema.org/draft-07/schema#", SchemaVersion.Draft7)]
        [InlineData("https://json-schema.org/draft/2019-09/schema", SchemaVersion.Draft201909)]
        [InlineData("http://json-schema.org/draft/2019-09/schema#", SchemaVersion.Draft201909)]
        public void RecognizesStandardSchemaUris(string uri, SchemaVersion expected)
        {
            Assert.True(SchemaConstants.TryGetDraft(uri, out SchemaVersion version));
            Assert.Equal(expected, version);

            var schema = new MPSchema(MPJson.Object(MPJson.Property("$schema", uri)));
            Assert.Equal(expected, schema.Version);
        }

        [Theory]
        [InlineData("http://example.com/my-schema")]
        [InlineData("http://json-schema.org/draft-03/schema#")]
        [InlineData("")]
        public void UnknownSchemaUriKeepsAllDrafts(string uri)
        {
            Assert.False(SchemaConstants.TryGetDraft(uri, out _));
            var schema = new MPSchema(MPJson.Object(MPJson.Property("$schema", uri)));
            Assert.Equal(SchemaVersion.All, schema.Version);
        }

        [Fact]
        public void MetaSchemaRefWithHttpsIsRecognized()
        {
            // A $ref to the 2019-09 meta-schema validates the instance as a schema
            var schema = Schema("{ '$ref': 'https://json-schema.org/draft/2019-09/schema' }");
            Assert.True(schema.Validate(MPJson.Parse("{ 'type': 'string' }")));
        }

        #endregion

        #region Keywords from other drafts are ignored

        private const string D4 = "'$schema': 'http://json-schema.org/draft-04/schema#'";
        private const string D6 = "'$schema': 'http://json-schema.org/draft-06/schema#'";
        private const string D7 = "'$schema': 'http://json-schema.org/draft-07/schema#'";
        private const string D2019 = "'$schema': 'https://json-schema.org/draft/2019-09/schema'";

        [Theory]
        // Later-draft keywords are unknown, and ignored, in earlier drafts
        [InlineData("{ " + D4 + ", 'const': 1 }", "2", true)]
        [InlineData("{ " + D4 + ", 'contains': { 'const': 1 } }", "[ 2 ]", true)]
        [InlineData("{ " + D4 + ", 'propertyNames': { 'maxLength': 1 } }", "{ 'abc': 1 }", true)]
        [InlineData("{ " + D6 + ", 'if': { 'const': 1 }, 'then': false }", "1", true)]
        [InlineData("{ " + D7 + ", 'dependentRequired': { 'a': [ 'b' ] } }", "{ 'a': 1 }", true)]
        [InlineData("{ " + D7 + ", 'dependentSchemas': { 'a': false } }", "{ 'a': 1 }", true)]
        [InlineData("{ " + D7 + ", 'contains': { 'const': 1 }, 'minContains': 0 }", "[ 2 ]", false)]
        [InlineData("{ " + D7 + ", 'contains': { 'const': 1 }, 'maxContains': 1 }", "[ 1, 1 ]", true)]
        [InlineData("{ " + D7 + ", '$defs': { 'a': { 'type': 'integer' } }, 'type': 'string' }", "'x'", true)]
        [InlineData("{ " + D7 + ", 'foo': 1, 'type': 'integer' }", "1", true)]
        [InlineData("{ " + D4 + ", '$comment': 'note', 'readOnly': true, 'type': 'integer' }", "1", true)]
        // The same keywords apply in the drafts that define them
        [InlineData("{ " + D6 + ", 'const': 1 }", "2", false)]
        [InlineData("{ " + D7 + ", 'if': { 'const': 1 }, 'then': false }", "1", false)]
        [InlineData("{ " + D2019 + ", 'dependentRequired': { 'a': [ 'b' ] } }", "{ 'a': 1 }", false)]
        [InlineData("{ " + D2019 + ", 'contains': { 'const': 1 }, 'minContains': 0 }", "[ 2 ]", true)]
        [InlineData("{ " + D2019 + ", 'contains': { 'const': 1 }, 'maxContains': 1 }", "[ 1, 1 ]", false)]
        // Without $schema, keywords from every draft apply and can be mixed
        [InlineData("{ 'id': 'http://example.com/s', 'const': 1 }", "1", true)]
        [InlineData("{ 'id': 'http://example.com/s', 'const': 1 }", "2", false)]
        [InlineData("{ 'maximum': 3, 'exclusiveMaximum': true, 'const': 3 }", "3", false)]
        public void KeywordsFollowTheDeclaredDraft(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Theory]
        // A known keyword with the wrong type for the draft is still an invalid schema
        [InlineData("{ " + D7 + ", 'maximum': 3, 'exclusiveMaximum': true }")]
        [InlineData("{ " + D4 + ", 'exclusiveMaximum': 3 }")]
        public void WrongTypeForDraftIsInvalid(string schema)
        {
            Assert.False(new MPSchema(MPJson.Parse(schema)).IsValid);
        }

        [Fact]
        public void IdKeywordFollowsTheDraft()
        {
            // draft 4 uses "id"; later drafts use "$id"
            var d4 = new MPSchema(MPJson.Parse("{ " + D4 + ", 'id': 'http://example.com/a', '$id': 'http://example.com/b' }"));
            Assert.Equal("http://example.com/a", d4.BaseUri?.ToString());

            var d7 = new MPSchema(MPJson.Parse("{ " + D7 + ", 'id': 'http://example.com/a', '$id': 'http://example.com/b' }"));
            Assert.Equal("http://example.com/b", d7.BaseUri?.ToString());
        }

        #endregion

        #region ECMA-262 regular expressions

        private static bool PatternMatches(string pattern, string value)
            => new MPSchema(MPJson.Object(MPJson.Property("pattern", pattern))).Validate(new MPJson(value));

        [Theory]
        // $ matches only at the end, not before a final newline
        [InlineData("^abc$", "abc", true)]
        [InlineData("^abc$", "abc\n", false)]
        // \d \w are ASCII only
        [InlineData(@"^\d$", "5", true)]
        [InlineData(@"^\d$", "\u0663", false)]   // ARABIC-INDIC DIGIT THREE
        [InlineData(@"^\D$", "\u0663", true)]
        [InlineData(@"^\w$", "_", true)]
        [InlineData(@"^\w$", "\u00e9", false)]   // e-acute
        [InlineData(@"^\W$", "\u00e9", true)]
        // \s is ECMA-262 whitespace: includes NBSP and BOM, excludes NEL
        [InlineData(@"^\s$", " ", true)]
        [InlineData(@"^\s$", "\u00a0", true)]
        [InlineData(@"^\s$", "\ufeff", true)]
        [InlineData(@"^\s$", "\u0085", false)]
        [InlineData(@"^\S$", "\u0085", true)]
        // Shorthand classes inside character classes, including negated ones
        [InlineData(@"^[\d_]+$", "12_3", true)]
        [InlineData(@"^[\d_]+$", "1\u0663", false)]
        [InlineData(@"^[\D]$", "a", true)]
        [InlineData(@"^[\D]$", "1", false)]
        [InlineData(@"^[^\s]+$", "a\u00a0b", false)]
        // \b uses ASCII word characters
        [InlineData(@"\bcaf\b", "caf\u00e9", true)]
        [InlineData(@"\bcafe\b", "a cafe", true)]
        [InlineData(@"\Bafe", "cafe", true)]
        // . matches a code point but not a line terminator
        [InlineData("^.$", "\n", false)]
        [InlineData("^.$", "\u2028", false)]
        [InlineData("^.$", "\ud83d\udc32", true)]      // U+1F432 is one code point
        [InlineData("^.{2}$", "\ud83d\udc32", false)]
        [InlineData("^\ud83d\udc32*$", "\ud83d\udc32\ud83d\udc32", true)] // quantifier applies to the whole character
        [InlineData(@"^\u{1F432}$", "\ud83d\udc32", true)]
        // [] matches nothing; [^] matches anything
        [InlineData("^[]$", "", false)]
        [InlineData("^a[]", "ab", false)]
        [InlineData("^[^]$", "\n", true)]
        // [ inside a class is a literal
        [InlineData("^[[a]+$", "[a[", true)]
        [InlineData("^[-[]+$", "-[", true)]
        [InlineData("^[^[]$", "[", false)]
        // Supported modern ECMA-262 syntax
        [InlineData(@"^(?<year>\d{4})-\k<year>$", "2020-2020", true)]
        [InlineData(@"(?<=\$)\d+", "$42", true)]
        [InlineData(@"^(a)\1$", "aa", true)]
        [InlineData(@"^\p{Lu}+$", "ABC", true)]
        [InlineData(@"^\cC$", "\u0003", true)]
        public void PatternUsesEcmaSemantics(string pattern, string value, bool expected)
        {
            Assert.Equal(expected, PatternMatches(pattern, value));
        }

        [Fact]
        public void UnpairedSurrogateIsOneCodePoint()
        {
            // Not theory data: xUnit cannot serialize strings with unpaired surrogates
            Assert.True(PatternMatches("^.$", "\ud83d"));
            Assert.True(PatternMatches("^..$", "\udc32\ud83d"));
            Assert.False(PatternMatches("^.$", "\udc32\ud83d"));
        }

        [Fact]
        public void PatternPropertiesUseEcmaSemantics()
        {
            var schema = new MPSchema(MPJson.Parse("{ 'patternProperties': { '^a$': { 'type': 'integer' } } }"));
            Assert.True(schema.Validate(MPJson.Parse("{ 'a\\n': 'x' }")));  // key "a\n" does not match ^a$
            Assert.False(schema.Validate(MPJson.Parse("{ 'a': 'x' }")));
        }

        [Theory]
        [InlineData(@"^\S(|(.|\n)*\S)\Z", false)] // .NET-only anchor
        [InlineData(@"\Aabc", false)]
        [InlineData(@"abc\z", false)]
        [InlineData("(?i)abc", false)]            // inline options
        [InlineData("(?>a+)b", false)]            // atomic group
        [InlineData("(?#comment)a", false)]
        [InlineData("(?'name'a)", false)]
        [InlineData("[a", false)]
        [InlineData("[a-[b]", false)]            // range a-[ runs backwards
        [InlineData(@"a\", false)]
        [InlineData(@"^\d{3}-\d{4}$", true)]
        [InlineData(@"(?<name>a)(?=b)(?!c)(?<=a)(?<!c)", true)]
        [InlineData("[]", true)]
        public void RegexFormatChecksEcmaSyntax(string pattern, bool expected)
        {
            Assert.Equal(expected, Formats.IsRegex(pattern));
            var schema = new MPSchema(MPJson.Parse("{ 'format': 'regex' }"));
            Assert.Equal(expected, schema.Validate(new MPJson(pattern)));
        }

        #endregion

        #region Draft 2019-09: $ref siblings, $anchor, $recursiveRef

        [Theory]
        [InlineData("{ " + D2019 + ", '$ref': '#/$defs/s', 'maxLength': 2, '$defs': { 's': { 'type': 'string' } } }", "'ab'", true)]
        [InlineData("{ " + D2019 + ", '$ref': '#/$defs/s', 'maxLength': 2, '$defs': { 's': { 'type': 'string' } } }", "'abc'", false)]
        [InlineData("{ " + D2019 + ", '$ref': '#/$defs/s', 'maxLength': 2, '$defs': { 's': { 'type': 'string' } } }", "1", false)]
        // Drafts 4-7, and schemas without $schema, still ignore keywords next to $ref
        [InlineData("{ " + D7 + ", '$ref': '#/definitions/s', 'maxLength': 2, 'definitions': { 's': { 'type': 'string' } } }", "'abc'", true)]
        [InlineData("{ '$ref': '#/definitions/s', 'maxLength': 2, 'definitions': { 's': { 'type': 'string' } } }", "'abc'", true)]
        // $anchor gives a subschema a plain-name fragment
        [InlineData("{ " + D2019 + ", '$ref': '#foo', '$defs': { 'a': { '$anchor': 'foo', 'type': 'integer' } } }", "1", true)]
        [InlineData("{ " + D2019 + ", '$ref': '#foo', '$defs': { 'a': { '$anchor': 'foo', 'type': 'integer' } } }", "'x'", false)]
        public void RefSiblingsAndAnchors(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        private const string RecursiveTree =
            "{ " + D2019 + ", '$recursiveAnchor': true, 'type': 'object', 'required': [ 'value' ], " +
            "'properties': { 'value': { 'type': 'integer' }, 'children': { 'type': 'array', 'items': { '$recursiveRef': '#' } } } }";

        [Theory]
        [InlineData("{ 'value': 1, 'children': [ { 'value': 2, 'children': [ { 'value': 3 } ] } ] }", true)]
        [InlineData("{ 'value': 1, 'children': [ { 'value': 2, 'children': [ { 'value': 'x' } ] } ] }", false)]
        [InlineData("{ 'value': 1, 'children': [ 5 ] }", false)]
        public void RecursiveRefResolvesToTheDocumentRoot(string data, bool expected)
        {
            Assert.Equal(expected, IsValid(RecursiveTree, data));
        }

        #endregion

        #region Draft 2019-09: unevaluatedProperties and unevaluatedItems

        [Theory]
        // Own properties / patternProperties / additionalProperties
        [InlineData("{ 'properties': { 'a': {} }, 'unevaluatedProperties': false }", "{ 'a': 1 }", true)]
        [InlineData("{ 'properties': { 'a': {} }, 'unevaluatedProperties': false }", "{ 'a': 1, 'b': 2 }", false)]
        [InlineData("{ 'patternProperties': { '^x': {} }, 'unevaluatedProperties': false }", "{ 'xa': 1 }", true)]
        [InlineData("{ 'patternProperties': { '^x': {} }, 'unevaluatedProperties': false }", "{ 'y': 1 }", false)]
        [InlineData("{ 'additionalProperties': true, 'unevaluatedProperties': false }", "{ 'z': 1 }", true)]
        [InlineData("{ 'unevaluatedProperties': { 'type': 'integer' } }", "{ 'a': 1 }", true)]
        [InlineData("{ 'unevaluatedProperties': { 'type': 'integer' } }", "{ 'a': 'x' }", false)]
        // allOf
        [InlineData("{ 'allOf': [ { 'properties': { 'a': {} } } ], 'unevaluatedProperties': false }", "{ 'a': 1 }", true)]
        [InlineData("{ 'allOf': [ { 'properties': { 'a': {} } } ], 'unevaluatedProperties': false }", "{ 'a': 1, 'b': 1 }", false)]
        // anyOf / oneOf: only branches that validate count
        [InlineData("{ 'anyOf': [ { 'properties': { 'a': { 'const': 1 } }, 'required': [ 'a' ] }, { 'properties': { 'b': { 'const': 1 } }, 'required': [ 'b' ] } ], 'unevaluatedProperties': false }", "{ 'a': 1, 'b': 1 }", true)]
        [InlineData("{ 'anyOf': [ { 'properties': { 'a': { 'const': 1 } }, 'required': [ 'a' ] }, { 'properties': { 'b': { 'const': 1 } }, 'required': [ 'b' ] } ], 'unevaluatedProperties': false }", "{ 'a': 1, 'b': 2 }", false)]
        [InlineData("{ 'oneOf': [ { 'properties': { 'a': {} }, 'required': [ 'a' ] }, { 'properties': { 'b': {} }, 'required': [ 'b' ] } ], 'unevaluatedProperties': false }", "{ 'a': 1 }", true)]
        // if / then / else
        [InlineData("{ 'if': { 'properties': { 't': { 'const': 'x' } }, 'required': [ 't' ] }, 'then': { 'properties': { 'x': {} } }, 'else': { 'properties': { 'y': {} } }, 'unevaluatedProperties': false }", "{ 't': 'x', 'x': 1 }", true)]
        [InlineData("{ 'if': { 'properties': { 't': { 'const': 'x' } }, 'required': [ 't' ] }, 'then': { 'properties': { 'x': {} } }, 'else': { 'properties': { 'y': {} } }, 'unevaluatedProperties': false }", "{ 't': 'x', 'y': 1 }", false)]
        [InlineData("{ 'if': { 'properties': { 't': { 'const': 'x' } }, 'required': [ 't' ] }, 'then': { 'properties': { 'x': {} } }, 'else': { 'properties': { 'y': {} } }, 'unevaluatedProperties': false }", "{ 'y': 1 }", true)]
        [InlineData("{ 'if': { 'properties': { 't': { 'const': 'x' } }, 'required': [ 't' ] }, 'then': { 'properties': { 'x': {} } }, 'else': { 'properties': { 'y': {} } }, 'unevaluatedProperties': false }", "{ 't': 'z', 'y': 1 }", false)]
        // $ref (2019-09 applies the sibling unevaluatedProperties)
        [InlineData("{ " + D2019 + ", '$ref': '#/$defs/a', '$defs': { 'a': { 'properties': { 'a': {} } } }, 'unevaluatedProperties': false }", "{ 'a': 1 }", true)]
        [InlineData("{ " + D2019 + ", '$ref': '#/$defs/a', '$defs': { 'a': { 'properties': { 'a': {} } } }, 'unevaluatedProperties': false }", "{ 'b': 1 }", false)]
        // dependentSchemas, nested unevaluatedProperties, not
        [InlineData("{ 'properties': { 'a': {} }, 'dependentSchemas': { 'a': { 'properties': { 'b': {} } } }, 'unevaluatedProperties': false }", "{ 'a': 1, 'b': 1 }", true)]
        [InlineData("{ 'properties': { 'a': {} }, 'dependentSchemas': { 'a': { 'properties': { 'b': {} } } }, 'unevaluatedProperties': false }", "{ 'b': 1 }", false)]
        [InlineData("{ 'allOf': [ { 'unevaluatedProperties': true } ], 'unevaluatedProperties': false }", "{ 'b': 1 }", true)]
        [InlineData("{ 'not': { 'not': { 'properties': { 'a': {} } } }, 'unevaluatedProperties': false }", "{ 'a': 1 }", false)]
        public void UnevaluatedProperties(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Theory]
        [InlineData("{ 'items': [ {} ], 'unevaluatedItems': false }", "[ 1 ]", true)]
        [InlineData("{ 'items': [ {} ], 'unevaluatedItems': false }", "[ 1, 2 ]", false)]
        [InlineData("{ 'items': {}, 'unevaluatedItems': false }", "[ 1, 2 ]", true)]
        [InlineData("{ 'items': [ {} ], 'additionalItems': true, 'unevaluatedItems': false }", "[ 1, 2 ]", true)]
        [InlineData("{ 'unevaluatedItems': { 'type': 'integer' } }", "[ 1, 2 ]", true)]
        [InlineData("{ 'unevaluatedItems': { 'type': 'integer' } }", "[ 1, 'x' ]", false)]
        [InlineData("{ 'allOf': [ { 'items': [ {}, {} ] } ], 'items': [ {} ], 'unevaluatedItems': false }", "[ 1, 2 ]", true)]
        [InlineData("{ 'allOf': [ { 'items': [ {}, {} ] } ], 'items': [ {} ], 'unevaluatedItems': false }", "[ 1, 2, 3 ]", false)]
        [InlineData("{ 'anyOf': [ { 'items': [ { 'const': 1 } ] }, { 'items': [ {}, { 'const': 2 } ] } ], 'unevaluatedItems': false }", "[ 1 ]", true)]
        [InlineData("{ 'anyOf': [ { 'items': [ { 'const': 1 } ] }, { 'items': [ {}, { 'const': 2 } ] } ], 'unevaluatedItems': false }", "[ 1, 2 ]", true)]
        [InlineData("{ 'anyOf': [ { 'items': [ { 'const': 1 } ] }, { 'items': [ {}, { 'const': 2 } ] } ], 'unevaluatedItems': false }", "[ 1, 3 ]", false)]
        // In 2019-09, contains does not evaluate items
        [InlineData("{ 'contains': { 'const': 1 }, 'unevaluatedItems': false }", "[ 1 ]", false)]
        public void UnevaluatedItems(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Fact]
        public void UnevaluatedKeywordsAreIgnoredBefore2019()
        {
            Assert.True(IsValid("{ " + D7 + ", 'properties': { 'a': {} }, 'unevaluatedProperties': false }", "{ 'b': 1 }"));
            Assert.True(IsValid("{ " + D7 + ", 'items': [ {} ], 'unevaluatedItems': false }", "[ 1, 2 ]"));
        }

        [Fact]
        public void UnevaluatedErrorsReportPathAndType()
        {
            var errors = new List<(ErrorType type, string path)>();
            var validator = new JsonValidator();
            validator.Validate(Schema("{ 'properties': { 'a': {} }, 'unevaluatedProperties': false }"),
                MPJson.Parse("{ 'a': 1, 'b': 2 }"), (s, e) => errors.Add((e.ErrorType, e.InstancePointer)));
            validator.Validate(Schema("{ 'items': [ {} ], 'unevaluatedItems': false }"),
                MPJson.Parse("[ 1, 2 ]"), (s, e) => errors.Add((e.ErrorType, e.InstancePointer)));

            Assert.Equal(new[] { (ErrorType.UnevaluatedProperties, "#/b"), (ErrorType.UnevaluatedItems, "#/1") }, errors);
        }

        #endregion

        #region multipleOf

        [Theory]
        [InlineData(0.3, 0.1, true)]
        [InlineData(0.075, 0.01, false)]
        [InlineData(0.0075, 0.0001, true)]
        [InlineData(19.99, 0.01, true)]
        [InlineData(4.5, 1.5, true)]
        [InlineData(10, 3, false)]
        [InlineData(-6, 3, true)]
        [InlineData(0, 7, true)]
        [InlineData(123456789012, 0.001, true)]
        [InlineData(1e-15, 1, false)]          // used to count as a multiple
        [InlineData(1e-20, 1e-21, true)]       // outside the exact decimal range
        [InlineData(3e-20, 1e-20, true)]
        [InlineData(1e308, 0.5, true)]         // quotient overflows
        public void MultipleOfIsExact(double value, double divisor, bool expected)
        {
            Assert.Equal(expected, JsonValidator.IsMultipleOf(value, divisor));
            var schema = new MPSchema(MPJson.Object(MPJson.Property("multipleOf", divisor)));
            Assert.Equal(expected, schema.Validate(new MPJson(value)));
        }

        #endregion

        #region Format regexes

        [Theory]
        [InlineData("ipv4", "192.168.0.1", true)]
        [InlineData("ipv4", "0.0.0.0", true)]
        [InlineData("ipv4", "255.255.255.255", true)]
        [InlineData("ipv4", "087.10.0.1", false)]          // leading zero
        [InlineData("ipv4", "256.1.1.1", false)]
        [InlineData("ipv4", "1.2.3.4\n", false)]           // trailing newline
        [InlineData("ipv4", "1.2.3.\u0663", false)]        // non-ASCII digit
        [InlineData("hostname", "example.com", true)]
        [InlineData("hostname", "a.b", true)]
        [InlineData("hostname", "example.com\n", false)]
        [InlineData("hostname", "a-.com", false)]          // label ends with a hyphen
        [InlineData("hostname", "-a.com", false)]
        [InlineData("email", "a@b.co", true)]
        [InlineData("email", "a@b.co\n", false)]
        [InlineData("uuid", "2eb8aa08-aa98-11ea-b4aa-73b441d16380", true)]
        [InlineData("uuid", "2eb8aa08-aa98-11ea-b4aa-73b441d16380\n", false)]
        [InlineData("duration", "P1D", true)]
        [InlineData("duration", "P1D\n", false)]
        [InlineData("relative-json-pointer", "0/a", true)]
        [InlineData("relative-json-pointer", "10#", true)]
        [InlineData("relative-json-pointer", "01/a", false)] // leading zero
        public void FormatRegexesAreExact(string format, string value, bool expected)
        {
            Assert.Equal(expected, Formats.IsValueOfFormat(value, format));
        }

        [Fact]
        public void HostnameLabelLength()
        {
            Assert.True(Formats.IsHostname(new string('a', 63) + ".com"));
            Assert.False(Formats.IsHostname(new string('a', 64) + ".com"));
        }

        #endregion

        #region IDN formats and content

        [Theory]
        [InlineData("\uC2E4\uB840.\uD14C\uC2A4\uD2B8", true)]           // example.test in Hangul
        [InlineData("\u302E\uC2E4\uB840.\uD14C\uC2A4\uD2B8", false)]   // U+302E is disallowed (first)
        [InlineData("\uC2E4\u302E\uB840.\uD14C\uC2A4\uD2B8", false)]   // U+302E is disallowed (inside)
        [InlineData("example.com", true)]
        [InlineData("l\u00B7l.com", true)]                               // middle dot between l's
        [InlineData("a\u00B7l.com", false)]
        [InlineData("\u03B1\u0375\u03B2", true)]                         // Greek numeral sign before Greek
        [InlineData("\u03B1\u0375a", false)]
        [InlineData("\u05D0\u05F3\u05D1", true)]                         // Hebrew geresh after Hebrew
        [InlineData("\u30A2\u30FB\u30A2", true)]                         // katakana middle dot with katakana
        [InlineData("a\u30FBb", false)]
        [InlineData("\u0661\u06F1", false)]                               // mixed Arabic-Indic digit sets
        [InlineData("\u0915\u094D\u200D\u0937", true)]                  // ZWJ after a virama
        [InlineData("a\u200Db", false)]                                  // ZWJ without a virama
        [InlineData("a\u0640b", false)]                                  // Arabic tatweel is disallowed
        [InlineData("", false)]
        public void IdnHostname(string value, bool expected)
        {
            Assert.Equal(expected, Formats.IsIdnHostname(value));
        }

        [Fact]
        public void IdnHostnameLabelTooLong()
        {
            // 30 different Hangul syllables encode to a Punycode label longer than 63 characters,
            // which UTS #46 processing rejects
            var label = new System.Text.StringBuilder();
            for (int i = 0; i < 30; i++)
                label.Append((char)(0xAC00 + i * 97));
            Assert.Throws<System.ArgumentException>(() => new System.Globalization.IdnMapping().GetAscii(label.ToString()));
            Assert.False(Formats.IsIdnHostname(label + ".com"));
            Assert.True(Formats.IsIdnHostname(label.ToString(0, 10) + ".com"));
        }

        [Theory]
        [InlineData("\uC2E4\uB840\uC2E4\uB840.\uD14C\uC2A4\uD2B8", false)] // no @
        [InlineData("\uC2E4\uB840@\uC2E4\uB840.\uD14C\uC2A4\uD2B8", true)]
        [InlineData("2962", false)]
        [InlineData("a@b", true)]
        [InlineData("@x.com", false)]
        [InlineData("a@", false)]
        [InlineData("a..b@x.com", false)]
        [InlineData("a b@x.com", false)]
        public void IdnEmail(string value, bool expected)
        {
            Assert.Equal(expected, Formats.IsIdnEmail(value));
        }

        private static List<ErrorType> ContentErrors(string schema, string data, bool validateContent)
        {
            var errors = new List<ErrorType>();
            new JsonValidator { ValidateContent = validateContent }
                .Validate(Schema(schema), MPJson.Parse(data), (s, e) => errors.Add(e.ErrorType));
            return errors;
        }

        private const string Base64Json = "{ " + D7 + ", 'contentEncoding': 'base64', 'contentMediaType': 'application/json' }";

        [Theory]
        [InlineData(Base64Json, "'eyJmb28iOiAiYmFyIn0='", null)]                          // {"foo": "bar"}
        [InlineData(Base64Json, "'eyJmb28iOi%iYmFyIn0='", ErrorType.ContentEncoding)]    // % is not base64
        [InlineData(Base64Json, "'ezp9Cg=='", ErrorType.ContentMediaType)]               // {:} is not JSON
        [InlineData("{ " + D7 + ", 'contentMediaType': 'application/json' }", "'{}'", null)]
        [InlineData("{ " + D7 + ", 'contentMediaType': 'application/json; charset=utf-8' }", "'{:}'", ErrorType.ContentMediaType)]
        [InlineData("{ " + D7 + ", 'contentMediaType': 'application/ld+json' }", "'[1'", ErrorType.ContentMediaType)]
        [InlineData("{ " + D7 + ", 'contentMediaType': 'text/plain' }", "'{:}'", null)]  // only JSON is checked
        [InlineData("{ " + D7 + ", 'contentEncoding': 'base32' }", "'!!'", null)]       // only base64 is checked
        [InlineData(Base64Json, "12", null)]                                              // only strings are checked
        [InlineData("{ " + D6 + ", 'contentMediaType': 'application/json' }", "'{:}'", null)] // not a draft 6 keyword
        public void ContentIsCheckedWhenEnabled(string schema, string data, ErrorType? expected)
        {
            var errors = ContentErrors(schema, data, validateContent: true);
            if (expected == null)
                Assert.Empty(errors);
            else
                Assert.Equal(new[] { expected.Value }, errors);
        }

        [Fact]
        public void ContentIsNotCheckedByDefault()
        {
            Assert.Empty(ContentErrors(Base64Json, "'eyJmb28iOi%iYmFyIn0='", validateContent: false));
            Assert.Empty(ContentErrors(Base64Json, "'ezp9Cg=='", validateContent: false));
        }

        #endregion
    }
}

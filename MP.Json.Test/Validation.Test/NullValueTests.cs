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
using MP.Json.Validation;
using Xunit;

namespace MP.Json.Test
{
    /// <summary>
    /// JSON null is stored as null; undefined (no value) is stored as DBNull.Value.
    /// </summary>
    public class NullValueTests
    {
        private static bool IsValid(string schema, string data)
            => new MPSchema(MPJson.Parse(schema)).Validate(MPJson.Parse(data));

        [Theory]
        [InlineData("{ 'const': null }", "null", true)]
        [InlineData("{ 'const': null }", "0", false)]
        [InlineData("{ 'const': 0 }", "null", false)]
        [InlineData("{ 'enum': [ 1, null ] }", "null", true)]
        [InlineData("{ 'enum': [ 1, false ] }", "null", false)]
        [InlineData("{ 'type': 'null' }", "null", true)]
        [InlineData("{ 'type': 'object' }", "null", false)]
        [InlineData("{ 'type': [ 'string', 'null' ] }", "null", true)]
        [InlineData("{ 'uniqueItems': true }", "[ null, null ]", false)]
        [InlineData("{ 'uniqueItems': true }", "[ null, 0, false, '' ]", true)]
        [InlineData("{ 'items': { 'type': 'null' } }", "[ null, null ]", true)]
        public void NullAsValue(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Theory]
        // A property whose value is null is present, not missing
        [InlineData("{ 'required': [ 'a' ] }", "{ 'a': null }", true)]
        [InlineData("{ 'properties': { 'a': { 'type': 'string' } } }", "{ 'a': null }", false)]
        [InlineData("{ 'properties': { 'a': { 'type': 'string' } } }", "{ }", true)]
        [InlineData("{ 'additionalProperties': false }", "{ 'a': null }", false)]
        [InlineData("{ 'dependencies': { 'a': [ 'b' ] } }", "{ 'a': null }", false)]
        [InlineData("{ 'dependencies': { 'a': [ 'b' ] } }", "{ 'a': null, 'b': null }", true)]
        [InlineData("{ 'minProperties': 1 }", "{ 'a': null }", true)]
        public void NullPropertyIsPresent(string schema, string data, bool expected)
        {
            Assert.Equal(expected, IsValid(schema, data));
        }

        [Fact]
        public void RefToNullIsNotAValidSchema()
        {
            // A $ref resolving to a JSON null (not a schema) resolves to nothing
            var schema = new MPSchema(MPJson.Parse("{ 'definitions': { 'a': null }, '$ref': '#/definitions/a' }"));
            Assert.False(schema.Validate(MPJson.Parse("1")));
        }

        [Fact]
        public void NullDefaultIsKeptAsMetadata()
        {
            var schema = new MPSchema(MPJson.Parse("{ 'default': null, 'type': 'integer' }"));
            MPJson value = schema.Root.GetMetadata("default");
            Assert.False(value.IsUndefined);
            Assert.Equal(JsonType.Null, value.Type);

            Assert.True(schema.Root.GetMetadata("missing").IsUndefined);
        }

        [Fact]
        public void TypeErrorOnNullReportsNullInstance()
        {
            var schema = new MPSchema(MPJson.Parse("{ 'type': 'string' }"));
            var errors = new List<(ErrorType type, MPJson actual, MPJson instance)>();
            new JsonValidator().Validate(schema, MPJson.Null,
                (sender, e) => errors.Add((e.ErrorType, e.Actual, e.Instance)));

            var error = Assert.Single(errors);
            Assert.Equal(ErrorType.Type, error.type);
            Assert.Equal("null", (string)error.actual); // the actual type name
            Assert.Equal(MPJson.Null, error.instance);
            Assert.False(error.instance.IsUndefined);
        }

        [Fact]
        public void RequiredErrorHasNoActualValue()
        {
            var schema = new MPSchema(MPJson.Parse("{ 'required': [ 'a' ] }"));
            var actuals = new List<MPJson>();
            new JsonValidator().Validate(schema, MPJson.Parse("{ }"), (sender, e) => actuals.Add(e.Actual));

            Assert.True(Assert.Single(actuals).IsUndefined);
        }

        [Fact]
        public void ConstNullErrorReportsNullValue()
        {
            var schema = new MPSchema(MPJson.Parse("{ 'const': 1 }"));
            var actuals = new List<MPJson>();
            new JsonValidator().Validate(schema, MPJson.Null, (sender, e) => actuals.Add(e.Actual));

            MPJson actual = Assert.Single(actuals);
            Assert.False(actual.IsUndefined);
            Assert.Equal(JsonType.Null, actual.Type);
        }
    }
}

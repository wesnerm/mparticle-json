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
using MP.Json;
using MP.Json.Validation;
using Xunit;

namespace JsonSchemaTestSuite.Draft6
{
    public class MinItems
    {

        /// <summary>
        ///     1 - minItems validation
        /// </summary>

        [Theory]
        [InlineData(
           "longer is valid",
           "[ 1, 2 ]",
           true
           )]

        [InlineData(
           "exact length is valid",
           "[ 1 ]",
           true
           )]

        [InlineData(
           "too short is invalid",
           "[ ]",
           false
           )]

        [InlineData(
           "ignores non-arrays",
           "''",
           true
           )]

        public void MinItemsValidation(string desc, string data, bool expected)
        {
            // minItems validation
            Console.Error.WriteLine(desc);
            string schemaData = "{ 'minItems':1 }";
            MPJson schemaJson = MPJson.Parse(schemaData);
            MPJson json = MPJson.Parse(data);
            MPSchema schema = schemaJson;
            var validator = new JsonValidator { Strict = true, Version = SchemaVersion.Draft6 };
            bool actual = validator.Validate(schema, json);
            Assert.Equal(expected, actual);
        }


    }
}

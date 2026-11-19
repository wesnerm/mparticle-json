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
using System.IO;
using System.Linq;
using MP.Json.Cli;
using Xunit;

namespace MP.Json.Test
{
    public class ValidatorCommandTests : IDisposable
    {
        private const string Schema =
            @"{ ""type"": ""object"", ""required"": [""id""],
                ""properties"": {
                  ""id"": { ""type"": ""integer"" },
                  ""name"": { ""type"": ""string"", ""pattern"": ""^[a-z]+$"" },
                  ""score"": { ""type"": ""number"", ""maximum"": 2.5 } } }";

        private readonly string dir;

        public ValidatorCommandTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "mpjson-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        private string WriteFile(string name, string contents)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private static (int exit, string stdout, string stderr) Run(string stdin, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int exit = ValidatorCommand.Run(args, new StringReader(stdin ?? ""), output, error);
            return (exit, output.ToString(), error.ToString());
        }

        private static MPJson[] ParseErrors(string stdout)
        {
            var json = MPJson.Parse(stdout);
            Assert.Equal(JsonType.Array, json.Type);
            return ((object[])json.Value).Select(MPJson.From).ToArray();
        }

        [Fact]
        public void ValidDataFromFileWritesEmptyArray()
        {
            string schema = WriteFile("schema.json", Schema);
            string data = WriteFile("data.json", @"{ ""id"": 1, ""name"": ""abc"", ""score"": 2 }");

            var (exit, stdout, stderr) = Run(null, schema, data);

            Assert.Equal(ValidatorCommand.ExitValid, exit);
            Assert.Equal("[]", stdout.Trim());
            Assert.Equal("", stderr);
        }

        [Fact]
        public void InvalidDataFromStdinWritesErrors()
        {
            string schema = WriteFile("schema.json", Schema);

            var (exit, stdout, _) = Run(@"{ ""name"": ""ABC"", ""score"": 3.75 }", schema);

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            var errors = ParseErrors(stdout);
            var types = errors.Select(e => (string)e["errorType"]).OrderBy(t => t).ToArray();
            Assert.Equal(new[] { "Maximum", "Pattern", "Required" }, types);

            var max = errors.Single(e => (string)e["errorType"] == "Maximum");
            Assert.Equal("#/score", (string)max["path"]);
            Assert.Equal(3.75, (double)max["value"]);
            Assert.Equal(2.5, (double)max["expected"]);

            var pattern = errors.Single(e => (string)e["errorType"] == "Pattern");
            Assert.Equal("ABC", (string)pattern["value"]);
            Assert.Equal("^[a-z]+$", (string)pattern["expected"]);
        }

        [Fact]
        public void DashReadsStdin()
        {
            string schema = WriteFile("schema.json", Schema);
            var (exit, stdout, _) = Run(@"{ ""id"": ""x"" }", schema, "-");

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            var error = Assert.Single(ParseErrors(stdout));
            Assert.Equal("Type", (string)error["errorType"]);
            Assert.Equal("#/id", (string)error["path"]);
        }

        [Fact]
        public void MaxErrorsLimitsOutput()
        {
            string schema = WriteFile("schema.json", Schema);
            var (exit, stdout, _) = Run(@"{ ""name"": ""ABC"", ""score"": 3.75 }", schema, "--max-errors", "1");

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            Assert.Single(ParseErrors(stdout));
        }

        [Fact]
        public void OutputDistinguishesNullFromNoValue()
        {
            string schema = WriteFile("schema.json",
                @"{ ""required"": [""id""], ""properties"": { ""name"": { ""const"": null }, ""age"": { ""const"": 1 } } }");
            var (exit, stdout, _) = Run(@"{ ""name"": ""x"", ""age"": null }", schema);

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            var errors = ParseErrors(stdout);

            // Required has no offending value, so "value" is omitted
            var required = errors.Single(e => (string)e["errorType"] == "Required");
            Assert.True(required["value"].IsUndefined);
            Assert.Equal("id", (string)required["expected"]);

            // const null expects a JSON null, which is written out
            var nameError = errors.Single(e => (string)e["path"] == "#/name");
            Assert.Equal("x", (string)nameError["value"]);
            Assert.Equal(MPJson.Null, nameError["expected"]);

            // a JSON null value is written as null
            var ageError = errors.Single(e => (string)e["path"] == "#/age");
            Assert.Equal(MPJson.Null, ageError["value"]);
            Assert.Equal(1.0, (double)ageError["expected"]);
        }

        [Fact]
        public void OutputEscapesStrings()
        {
            string schema = WriteFile("schema.json", @"{ ""properties"": { ""a"": { ""maxLength"": 1 } } }");
            var (exit, stdout, _) = Run("{ \"a\": \"q\\\"\\\\\\n\\u0001\" }", schema);

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            var error = Assert.Single(ParseErrors(stdout));
            Assert.Equal("q\"\\\n\u0001", (string)error["value"]);
        }

        [Fact]
        public void SampleBadEventProducesErrors()
        {
            string schema = Path.Combine("sampledata", "event-level", "custom_event_schema.json");
            string data = Path.Combine("sampledata", "event-level", "custom_event_bad.json");

            var (exit, stdout, _) = Run(null, schema, data);

            Assert.Equal(ValidatorCommand.ExitInvalid, exit);
            var types = ParseErrors(stdout).Select(e => (string)e["errorType"]).ToArray();
            Assert.Contains("AdditionalProperties", types);
            Assert.Contains("Pattern", types);
            Assert.DoesNotContain("TimeLimit", types);
        }

        [Theory]
        [InlineData("")]
        [InlineData("a.json b.json c.json")]
        [InlineData("--bogus a.json")]
        [InlineData("a.json --max-errors")]
        [InlineData("a.json --max-errors 0")]
        public void BadArgumentsReturnErrorExitCode(string commandLine)
        {
            var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (exit, stdout, stderr) = Run(null, args);

            Assert.Equal(ValidatorCommand.ExitError, exit);
            Assert.Equal("", stdout);
            Assert.StartsWith("mpjson:", stderr);
        }

        [Fact]
        public void HelpWritesUsage()
        {
            var (exit, stdout, _) = Run(null, "--help");
            Assert.Equal(ValidatorCommand.ExitValid, exit);
            Assert.Contains("Usage: mpjson", stdout);
        }

        [Fact]
        public void MissingFileReturnsErrorExitCode()
        {
            var (exit, stdout, stderr) = Run(null, Path.Combine(dir, "missing.json"), "-");
            Assert.Equal(ValidatorCommand.ExitError, exit);
            Assert.Equal("", stdout);
            Assert.Contains("cannot read schema", stderr);
        }

        [Fact]
        public void MalformedJsonReturnsErrorExitCode()
        {
            string schema = WriteFile("schema.json", Schema);
            var (exit, stdout, stderr) = Run("{ \"id\": ", schema);

            Assert.Equal(ValidatorCommand.ExitError, exit);
            Assert.Equal("", stdout);
            Assert.Contains("not valid JSON", stderr);
        }
    }
}

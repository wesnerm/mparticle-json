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
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MP.Json.Validation;

namespace MP.Json.Cli
{
    /// <summary>
    /// Validates a JSON document against a JSON Schema and writes the
    /// validation errors to standard output as a JSON array.
    /// </summary>
    public static class ValidatorCommand
    {
        /// <summary>The document is valid; an empty array was written.</summary>
        public const int ExitValid = 0;

        /// <summary>The document is invalid; the errors were written.</summary>
        public const int ExitInvalid = 1;

        /// <summary>Bad arguments, unreadable files, malformed JSON or an unusable schema.</summary>
        public const int ExitError = 2;

        public const string Usage =
@"Usage: mpjson <schema.json> [data.json | -] [options]

Validates a JSON document against a JSON Schema and writes a JSON array of
validation errors to standard output. The document is read from standard
input when no data file is given or the data file is '-'.

Options:
  -m, --max-errors <n>   Stop after reporting n errors (default: all)
  -c, --coerce           Allow strings to be coerced to numbers/booleans
  -h, --help             Show this help

Exit codes:
  0  valid (writes [])
  1  invalid (writes the errors)
  2  usage error, unreadable file, malformed JSON or invalid schema";

        /// <summary>
        /// Runs the command. All I/O goes through the supplied readers and writers.
        /// </summary>
        public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            if (stdin == null) throw new ArgumentNullException(nameof(stdin));
            if (stdout == null) throw new ArgumentNullException(nameof(stdout));
            if (stderr == null) throw new ArgumentNullException(nameof(stderr));

            if (!TryParseArguments(args, stderr, out var options))
                return ExitError;

            if (options.Help)
            {
                stdout.WriteLine(Usage);
                return ExitValid;
            }

            if (!TryReadJson(options.SchemaPath, null, stderr, "schema", out MPJson schemaJson))
                return ExitError;

            string dataPath = options.DataPath == "-" ? null : options.DataPath;
            if (!TryReadJson(dataPath, stdin, stderr, "data", out MPJson data))
                return ExitError;

            var schema = new MPSchema(schemaJson);
            if (!schema.IsValid)
            {
                stderr.WriteLine($"mpjson: '{options.SchemaPath}' is not a valid JSON Schema.");
                return ExitError;
            }

            var errors = Validate(schema, data, options);
            ErrorWriter.Write(stdout, errors);
            return errors.Count == 0 ? ExitValid : ExitInvalid;
        }

        /// <summary>
        /// Validates the data against the schema, returning the errors in a serializable form.
        /// </summary>
        public static IReadOnlyList<ReportedError> Validate(MPSchema schema, MPJson data, CliOptions options)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            options = options ?? new CliOptions();
            WarmUpRegexEngine();

            var errors = new List<ReportedError>();
            var validator = new JsonValidator
            {
                CoerceStringsToValues = options.CoerceStringsToValues,
            };

            validator.Validate(schema, data, (sender, e) =>
            {
                // ValidationArgs is reused between callbacks, so capture everything now.
                errors.Add(new ReportedError
                {
                    ErrorType = e.ErrorType.ToString(),
                    Path = e.InstancePointer,
                    SchemaPath = e.SchemaPointer,
                    Value = e.Actual.Value,
                    Expected = e.Expected,
                });

                if (errors.Count >= options.MaxErrors)
                    e.CancelValidation();
            });

            return errors;
        }

        private static bool regexWarmedUp;

        /// <summary>
        /// The library gives each schema "pattern" a 0.5ms match timeout. In a fresh process
        /// the first match also pays for JIT-compiling the regex engine, which can exceed that
        /// budget and produce a spurious TimeLimit error that cancels validation. Running one
        /// throwaway match first keeps a short-lived CLI process deterministic.
        /// </summary>
        private static void WarmUpRegexEngine()
        {
            if (regexWarmedUp) return;
            regexWarmedUp = true;
            var options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Singleline;
            new Regex("^[0-9a-z]*(x|y)+$", options, TimeSpan.FromSeconds(5)).IsMatch("123abcxy");
            new Regex("^[0-9a-z]*(x|y)+$", options, TimeSpan.FromSeconds(5)).IsMatch("123abc-");
        }

        private static bool TryParseArguments(string[] args, TextWriter stderr, out CliOptions options)
        {
            options = new CliOptions();
            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        options.Help = true;
                        return true;

                    case "-c":
                    case "--coerce":
                        options.CoerceStringsToValues = true;
                        break;

                    case "-m":
                    case "--max-errors":
                        if (i + 1 >= args.Length
                            || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int max)
                            || max < 1)
                        {
                            stderr.WriteLine($"mpjson: {arg} requires a positive integer.");
                            return false;
                        }
                        options.MaxErrors = max;
                        i++;
                        break;

                    default:
                        if (arg.Length > 1 && arg[0] == '-')
                        {
                            stderr.WriteLine($"mpjson: unknown option '{arg}'.");
                            stderr.WriteLine(Usage);
                            return false;
                        }
                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count < 1 || positional.Count > 2)
            {
                stderr.WriteLine(positional.Count < 1
                    ? "mpjson: missing schema file."
                    : "mpjson: too many arguments.");
                stderr.WriteLine(Usage);
                return false;
            }

            options.SchemaPath = positional[0];
            options.DataPath = positional.Count > 1 ? positional[1] : null;
            return true;
        }

        /// <summary>
        /// Reads and parses JSON from a file, or from <paramref name="fallback"/> when path is null.
        /// </summary>
        private static bool TryReadJson(string path, TextReader fallback, TextWriter stderr,
            string what, out MPJson json)
        {
            json = default;
            string source = path ?? "standard input";
            var parser = new JsonParser();

            try
            {
                bool ok;
                if (path != null)
                {
                    using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                    ok = parser.TryParse(reader, out json);
                }
                else
                {
                    ok = parser.TryParse(fallback, out json);
                }

                if (!ok)
                {
                    stderr.WriteLine(
                        $"mpjson: {what} in {source} is not valid JSON: {parser.ErrorMessage} " +
                        $"(line {parser.Line}, column {parser.Column}).");
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException || ex is NotSupportedException)
            {
                stderr.WriteLine($"mpjson: cannot read {what} file '{source}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Command-line options
    /// </summary>
    public class CliOptions
    {
        public string SchemaPath { get; set; }
        public string DataPath { get; set; }
        public int MaxErrors { get; set; } = int.MaxValue;
        public bool CoerceStringsToValues { get; set; }
        public bool Help { get; set; }
    }

    /// <summary>
    /// A validation error captured for output
    /// </summary>
    public class ReportedError
    {
        /// <summary>Keyword that failed, e.g. "Required" or "Type"</summary>
        public string ErrorType { get; set; }

        /// <summary>JSON Pointer to the failing location in the data</summary>
        public string Path { get; set; }

        /// <summary>JSON Pointer to the failing keyword in the schema</summary>
        public string SchemaPath { get; set; }

        /// <summary>The offending value (or a related value, such as the actual type name)</summary>
        public object Value { get; set; }

        /// <summary>What the schema expected, when it can be expressed as JSON</summary>
        public object Expected { get; set; }
    }
}

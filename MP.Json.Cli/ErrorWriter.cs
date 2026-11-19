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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MP.Json.Validation;

namespace MP.Json.Cli
{
    /// <summary>
    /// Writes validation errors as a JSON array, one error object per line.
    /// </summary>
    /// <remarks>
    /// MPJson.ToString is not used for values because it formats numbers with the
    /// current culture and writes 'undefined', neither of which is valid JSON.
    /// </remarks>
    public static class ErrorWriter
    {
        private const int MaxDepth = 64;

        public static void Write(TextWriter writer, IReadOnlyList<ReportedError> errors)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (errors == null) throw new ArgumentNullException(nameof(errors));

            if (errors.Count == 0)
            {
                writer.WriteLine("[]");
                return;
            }

            writer.WriteLine("[");
            for (int i = 0; i < errors.Count; i++)
            {
                var error = errors[i];
                writer.Write("  {");
                WriteProperty(writer, "errorType", error.ErrorType, first: true);
                WriteProperty(writer, "path", error.Path);
                WriteProperty(writer, "schemaPath", error.SchemaPath);
                if (IsJsonValue(error.Value, 0))
                    WriteProperty(writer, "value", error.Value);
                if (IsJsonValue(error.Expected, 0))
                    WriteProperty(writer, "expected", error.Expected);
                writer.Write(" }");
                writer.WriteLine(i < errors.Count - 1 ? "," : "");
            }
            writer.WriteLine("]");
        }

        private static void WriteProperty(TextWriter writer, string name, object value, bool first = false)
        {
            if (!first) writer.Write(',');
            writer.Write(' ');
            WriteString(writer, name);
            writer.Write(": ");
            WriteValue(writer, value);
        }

        /// <summary>
        /// Whether the value can be written as JSON (rejects undefined and non-JSON objects
        /// such as subschemas).
        /// </summary>
        private static bool IsJsonValue(object value, int depth)
        {
            if (depth > MaxDepth) return false;
            switch (value)
            {
                case DBNull _:
                    return false; // undefined: no value to write
                case null: // JSON null
                case string _:
                case bool _:
                case double _:
                case float _:
                case decimal _:
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    return true;
                case MPJson json:
                    return json.HasValue && IsJsonValue(json.Value, depth);
                case IEnumerable<KeyValuePair<string, object>> map:
                    foreach (var kv in map)
                        if (!IsJsonValue(kv.Value, depth + 1)) return false;
                    return true;
                case IEnumerable list:
                    foreach (object item in list)
                        if (!IsJsonValue(item, depth + 1)) return false;
                    return true;
                default:
                    return false;
            }
        }

        private static void WriteValue(TextWriter writer, object value)
        {
            switch (value)
            {
                case null:
                    writer.Write("null");
                    break;
                case string s:
                    WriteString(writer, s);
                    break;
                case bool b:
                    writer.Write(b ? "true" : "false");
                    break;
                case double d:
                    WriteDouble(writer, d);
                    break;
                case float f:
                    WriteDouble(writer, f);
                    break;
                case IFormattable number when !(value is Enum):
                    writer.Write(number.ToString(null, CultureInfo.InvariantCulture));
                    break;
                case MPJson json:
                    WriteValue(writer, json.Value);
                    break;
                case IEnumerable<KeyValuePair<string, object>> map:
                {
                    writer.Write('{');
                    bool first = true;
                    foreach (var kv in map)
                    {
                        if (!first) writer.Write(", ");
                        WriteString(writer, kv.Key);
                        writer.Write(": ");
                        WriteValue(writer, kv.Value);
                        first = false;
                    }
                    writer.Write('}');
                    break;
                }
                case IEnumerable list:
                {
                    writer.Write('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) writer.Write(", ");
                        WriteValue(writer, item);
                        first = false;
                    }
                    writer.Write(']');
                    break;
                }
                default:
                    WriteString(writer, value.ToString());
                    break;
            }
        }

        private static void WriteDouble(TextWriter writer, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                writer.Write("null");
            else
                writer.Write(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(TextWriter writer, string s)
        {
            writer.Write('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': writer.Write("\\\""); break;
                    case '\\': writer.Write("\\\\"); break;
                    case '\n': writer.Write("\\n"); break;
                    case '\r': writer.Write("\\r"); break;
                    case '\t': writer.Write("\\t"); break;
                    case '\b': writer.Write("\\b"); break;
                    case '\f': writer.Write("\\f"); break;
                    default:
                        if (ch < ' ' || ch == (char)0x2028 || ch == (char)0x2029)
                            writer.Write("\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            writer.Write(ch);
                        break;
                }
            }
            writer.Write('"');
        }
    }
}

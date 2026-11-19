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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using static MP.Json.Validation.BitUtils;
using System.Collections.Generic;
using System.Text;

namespace MP.Json.Validation
{

    /// <summary>
    /// Reusable non-thread-safe schema validator.
    /// 
    /// This is not thread-safe, so you need one per thread. However, this can be reused for successive
    /// validations in one thread. Just call Init(document, object).
    /// </summary>
    public class JsonValidator
    {
        #region Variables
        const int MaxTimeToLive = 1000 * 1000;

        private EventHandler<ValidationArgs> errorAction;

        private readonly ValidationArgs validationArgs;

        private StringInfo _stringInfo;

        private int suppressionLevel;

        private int timeToLive;

        /// <summary>
        /// $ref targets currently being evaluated, with the instance depth at which each
        /// was entered. Used to detect reference cycles that make no progress.
        /// </summary>
        private readonly List<(Subschema schema, int instanceDepth)> activeRefs
            = new List<(Subschema, int)>();

        #endregion

        #region Construction
        public JsonValidator()
        {
            validationArgs = new ValidationArgs(this);
        }
        #endregion

        #region Properties

        public readonly JsonPointer SchemaPointer = new JsonPointer();

        public readonly JsonPointer InstancePointer = new JsonPointer();

        /// <summary>
        /// Strict compliance at the cost of performance.
        /// </summary>
        /// <remarks>
        /// No longer affects validation: minLength and maxLength always count Unicode
        /// code points, as JSON Schema requires. Kept for compatibility.
        /// </remarks>
        public bool Strict { get; set; }

        /// <summary>
        /// Number of Unicode code points in a string. A surrogate pair counts as one;
        /// an unpaired surrogate counts as one.
        /// </summary>
        internal static int CodePointLength(string s)
        {
            int length = s.Length;
            for (int i = 0; i < s.Length - 1; i++)
            {
                if (char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]))
                {
                    length--;
                    i++;
                }
            }
            return length;
        }

        /// <summary>
        /// Enable coercion of string values to numbers and booleans
        /// </summary>
        public bool CoerceStringsToValues { get; set; }

        /// <summary>
        /// Validates string content against contentEncoding and contentMediaType (draft 7+).
        /// </summary>
        /// <remarks>
        /// Off by default: JSON Schema defines these keywords as annotations, not assertions.
        /// When on, "base64" encoding and JSON media types ("application/json" and "+json")
        /// are checked; other encodings and media types are not.
        /// </remarks>
        public bool ValidateContent { get; set; }

        /// <summary>
        /// Whether validation should abort on first error
        /// </summary>
        public bool AbortOnError { get; set; }

        public SchemaVersion Version { get; set; } = SchemaVersion.All;

        /// <summary>
        /// For correctly reading string lengths
        /// </summary>
        public StringInfo StringInfo
        {
            get
            {
                if (_stringInfo == null)
                    _stringInfo = new StringInfo();
                return _stringInfo;
            }
        }

        /// <summary>
        /// Indicates whether the validation was successful
        /// </summary>
        public bool Success { get; private set; }

        #endregion

        #region Methods

        internal int TurnOffErrors()
        {
            return suppressionLevel++;
        }

        internal void TurnOnErrors()
        {
            suppressionLevel--;
        }

        internal void RestoreSuppressionLevel(int level)
        {
            suppressionLevel = level;
        }

        internal bool IsRecordingErrors
            => suppressionLevel == 0 && errorAction != null;


        // Error values are raw JSON values: null is a JSON null and DBNull.Value means
        // "no value". The overloads without an instance use the actual value as the instance.

        internal bool ReportError(ErrorType errorType, Subschema schema, object actual)
            => ReportError(errorType, schema, actual, null, actual);

        internal bool ReportError(ErrorType errorType,
            Subschema schema,
            object actual,
            object expected,
            object instance)
        {
            var result = ReportError((Keyword)errorType, schema, actual, expected, instance);
            if (errorType == ErrorType.TimeLimit) CancelValidation();
            return result;
        }

        internal bool ReportError(Keyword keyword, Subschema schema, object actual, object expected = null)
            => ReportError(keyword, schema, actual, expected, actual);

        /// <summary>
        /// Pushes an error onto the errors list with the given parameters
        /// </summary>
        /// <param name="keyword">failing keyword</param>
        /// <param name="schema">failing subschema</param>
        /// <param name="actual">offending or related value (null is a JSON null; DBNull.Value for none)</param>
        /// <param name="expected">expected value; defaults to the keyword's data in the schema</param>
        /// <param name="instance">instance where the failure occurred</param>
        internal bool ReportError(Keyword keyword,
            Subschema schema,
            object actual,
            object expected,
            object instance)
        {
            // Errors being suppressed don't directly affect success result
            if (suppressionLevel > 0 || errorAction == null)
                return true;

            if (schema != null)
            {
                if (expected == null)
                    expected = schema.GetData(keyword);
            }

            validationArgs.Actual = MPJson.From(actual);
            validationArgs.Instance = MPJson.From(instance);
            validationArgs.Expected = expected;
            validationArgs.Schema = schema;
            validationArgs.ValidationError = null;

            if (keyword >= 0)
            {
                validationArgs.ErrorType = (ErrorType)keyword;
                SchemaPointer.Push(keyword);
            }
            else
            {
                validationArgs.ErrorType = (ErrorType)SchemaPointer.Keyword;
            }

            errorAction(this, validationArgs);
            if (keyword >= 0) SchemaPointer.Pop();

            if (AbortOnError)
                errorAction = null;

            return errorAction == null;
        }

        [DebuggerStepThrough]
        internal bool OutOfTime()
        {
            if (timeToLive-- > 0)
                return false;

            ReportError(ErrorType.TimeLimit, null, DBNull.Value);
            CancelValidation();
            return true;
        }

        public bool Validate(MPSchema schema, MPJson json, EventHandler<ValidationArgs> eventHandler = null)
        {
            return Validate(schema?.Root, json, eventHandler);
        }

        public bool Validate(Subschema root, MPJson json, EventHandler<ValidationArgs> eventHandler = null)
        {
            if (root == null)
                return false;

            object jobject = json.Value;

            SchemaPointer.Clear();
            InstancePointer.Clear();
            activeRefs.Clear();

            suppressionLevel = 0;

            errorAction = eventHandler;
            timeToLive = MaxTimeToLive;
            Success = Validate(root, jobject);
            return Success;
        }

        public void CancelValidation()
        {
            errorAction = null;
        }

        #endregion

        #region Validation

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Validate(Subschema schema, object instance)
        {
            return !OutOfTime()
                && ValidateType(schema, instance)
                && ValidateGeneric(schema, instance);
        }

        private bool Validate(Subschema schema, object instance,
            Keyword keyword, string prop1, string prop2)
        {
            SchemaPointer.Push(keyword);
            if (prop1 != null) SchemaPointer.Push(prop1);
            if (prop2 != null) InstancePointer.Push(prop2);

            bool result = Validate(schema, instance);

            SchemaPointer.Pop();
            if (prop1 != null) SchemaPointer.Pop();
            if (prop2 != null) InstancePointer.Pop();
            return result;
        }

        private bool Validate(Subschema schema, object instance, Keyword keyword)
        {
            SchemaPointer.Push(keyword);
            bool result = Validate(schema, instance);
            SchemaPointer.Pop();
            return result;
        }

        private bool Validate(Subschema schema, object instance, int index)
        {
            SchemaPointer.Push(index);
            bool result = Validate(schema, instance);
            SchemaPointer.Pop();
            return result;
        }

        private bool Validate(Subschema schema, object instance, int index, int instanceIndex)
        {
            if (index >= 0) SchemaPointer.Push(index);
            InstancePointer.Push(instanceIndex);
            bool result = Validate(schema, instance);
            InstancePointer.Pop();
            if (index >= 0) SchemaPointer.Pop();
            return result;
        }

        private bool ValidateType(Subschema schema, object value)
        {
            var type = MPJson.GetType(value);
            switch (type)
            {
                case JsonType.Object:
                    if ((schema.Flags & SchemaFlags.TypeObject) == 0)
                        break;
                    return ValidateObject(schema, (KeyValuePair<string, object>[])value);

                case JsonType.Array:
                    if ((schema.Flags & SchemaFlags.TypeArray) == 0)
                        break;
                    return ValidateArray(schema, (object[])value);

                case JsonType.Number:
                    double d = Convert.ToDouble(value);
                    if ((schema.Flags & SchemaFlags.TypeNumber) == 0
                        && ((schema.Flags & SchemaFlags.TypeInteger) == 0 || d != Math.Floor(d)))
                        break;
                    return ValidateNumber(schema, d);

                case JsonType.String:
                    if ((schema.Flags & SchemaFlags.TypeString) != 0)
                        return ValidateString(schema, (string)value);

                    if (this.CoerceStringsToValues)
                    {
                        var str = (string)value;
                        if ((schema.Flags & (SchemaFlags.TypeNumber | SchemaFlags.TypeInteger)) != 0
                            && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                            && (0 != (schema.Flags & SchemaFlags.TypeNumber) || d == Math.Floor(d)))
                            return ValidateNumber(schema, d);

                        if ((schema.Flags & SchemaFlags.TypeBoolean) != 0
                            && string.Equals(str, str.Length == 4 ? JsonParser.TrueKeyword : JsonParser.FalseKeyword, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    break;

                case JsonType.Boolean:
                    if ((schema.Flags & SchemaFlags.TypeBoolean) == 0)
                        break;
                    return true;

                case JsonType.Null:
                    if ((schema.Flags & SchemaFlags.TypeNull) == 0)
                        break;
                    return true;
            }

            if (!IsRecordingErrors)
                return false;

            return (schema.Flags & SchemaFlags.TypeAll) != 0
                ? ReportError(
                    ErrorType.Type,
                    schema,
                    actual: KeywordUtils.GetTypeText(type),
                    expected: schema.GetValidTypes(),
                    instance: value)
                : ReportError(ErrorType.None, null, value);
        }

        private bool ValidateGeneric(Subschema schema, object instance)
        {
            bool finalResult = true;

            for (SchemaFlags mask = schema.Flags & (SchemaFlags.GenericProperties & SchemaFlags.AllProperties);
                    mask != 0;
                    mask = (SchemaFlags)RemoveLowestBit((long)mask))
            {
                Keyword keyword = (Keyword)IndexOfLowestBit((long)mask);

                bool? result = null;
                Subschema[] schemaArray;
                int index;
                switch (keyword)
                {
                    case Keyword.If:
                        int level = TurnOffErrors();
                        result = Validate(schema.If, instance, Keyword.If);
                        RestoreSuppressionLevel(level);

                        result = result == true
                            ? Validate(schema.Then, instance, Keyword.Then)
                            : Validate(schema.Else, instance, Keyword.Else);
                        break;

                    case Keyword.Not:
                        level = TurnOffErrors();
                        result = !Validate(schema.Not, instance, Keyword.Not);
                        RestoreSuppressionLevel(level);
                        break;

                    case Keyword.AllOf:
                        result = true;
                        schemaArray = schema.AllOf;
                        SchemaPointer.Push(keyword);
                        for (index = 0; index < schemaArray.Length; index++)
                            if (!Validate(schemaArray[index], instance, index))
                            {
                                finalResult = false;
                                if (!IsRecordingErrors) break;
                            }
                        SchemaPointer.Pop();
                        break;

                    case Keyword.AnyOf:
                        result = false;
                        level = TurnOffErrors();
                        schemaArray = schema.AnyOf;
                        SchemaPointer.Push(keyword);
                        for (index = 0; index < schemaArray.Length; index++)
                            if (Validate(schemaArray[index], instance, index))
                            {
                                result = true;
                                break;
                            }
                        SchemaPointer.Pop();
                        RestoreSuppressionLevel(level);
                        break;

                    case Keyword.OneOf:
                        result = false;
                        level = TurnOffErrors();
                        schemaArray = schema.OneOf;
                        SchemaPointer.Push(keyword);
                        for (index = 0; index < schemaArray.Length; index++)
                            if (Validate(schemaArray[index], instance, index))
                            {
                                result = !result;
                                if (result == false)
                                    break;
                            }
                        SchemaPointer.Pop();
                        RestoreSuppressionLevel(level);
                        break;

                    case Keyword.Enum:
                        result = false;
                        // TODO: Provide support for type coercion
                        foreach (object v in (object[])schema.Enum.Value)
                            if (MPJson.Matches(v, instance))
                            {
                                result = true;
                                break;
                            }
                        break;

                    case Keyword.Const:
                        // TODO: Provide support for string to value coercion
                        result = MPJson.Matches(schema.Const.Value, instance);
                        break;

                    case Keyword._Ref:
                        var r = schema.Ref;
                        if (r.Version != 0)
                        {
                            var draft = new MPSchema(MPJson.From(instance), r.Version);
                            result = draft.IsValid;
                            break; // This was checked on creation
                        }

                        var rSchema = r.Schema;
                        int instanceDepth = InstancePointer.Length;
                        result = rSchema != null && !IsReferenceCycle(rSchema, instanceDepth);
                        if (result == true)
                        {
                            activeRefs.Add((rSchema, instanceDepth));
                            bool refResult = Validate(rSchema, instance, Keyword._Ref);
                            activeRefs.RemoveAt(activeRefs.Count - 1);
                            if (!refResult)
                                finalResult = false;
                        }
                        break;

                    case Keyword.Metadata:
                        if (ValidateContent && instance is string text && !CheckContent(schema, text))
                        {
                            finalResult = false;
                            if (!IsRecordingErrors)
                                return false;
                        }
                        break;
                }

                if (result == false)
                {
                    finalResult = false;
                    if (ReportError(keyword, schema, instance))
                        return false;
                }
            }

            return finalResult;
        }

        /// <summary>
        /// Whether following a $ref to <paramref name="target"/> would loop forever.
        /// </summary>
        /// <remarks>
        /// Instance depth never decreases while descending, so if the same $ref target is
        /// already being evaluated at the same instance depth, it is being applied to the
        /// same instance node again without any progress: an infinite loop. Any other
        /// nesting of $ref, allOf, anyOf, etc. is allowed, however deep the schema path.
        /// </remarks>
        private bool IsReferenceCycle(Subschema target, int instanceDepth)
        {
            for (int i = activeRefs.Count - 1; i >= 0; i--)
            {
                var active = activeRefs[i];
                if (active.instanceDepth < instanceDepth)
                    break; // entered at a shallower depth, so progress has been made since
                if (active.schema == target)
                    return true;
            }
            return false;
        }

        private bool ValidateObject(Subschema schema, KeyValuePair<string, object>[] map)
        {
            bool finalResult = true;
            bool propertyCheck = false;

            for (var mask = schema.Flags & SchemaFlags.ObjectProperties;
                    mask != 0;
                    mask = (SchemaFlags)RemoveLowestBit((long)mask))
            {
                Keyword keyword = (Keyword)IndexOfLowestBit((long)mask);
                Subschema subschema;

                bool result = true;
                switch (keyword)
                {
                    case Keyword.DependentSchemas:
                        foreach (var kv in schema.DependentSchemas)
                        {
                            int index = map.GetPropertyIndex(kv.Key);
                            if (index < 0) continue;
                            if (!Validate(kv.Value, map,
                                Keyword.DependentSchemas, kv.Key, null))
                            {
                                finalResult = false;
                                if (!IsRecordingErrors)
                                    return false;
                            }
                        }
                        break;

                    case Keyword.DependentRequired:
                        foreach (var kv in schema.DependentRequired)
                        {
                            int index = map.GetPropertyIndex(kv.Key);
                            if (index >= 0 && !HandleRequired(schema, map, kv.Value,
                                Keyword.DependentRequired, kv.Key))
                            {
                                finalResult = false;
                                if (!IsRecordingErrors)
                                    return false;
                            }
                        }
                        break;

                    case Keyword.MaxProperties:
                        result = map.Length <= schema.MaxProperties;
                        break;

                    case Keyword.MinProperties:
                        result = map.Length >= schema.MinProperties;
                        break;

                    case Keyword.PropertyNames:
                        subschema = schema.PropertyNames;
                        for (int i = 0; i < map.Length; i++)
                        {
                            var kv = map[i];
                            if (!Validate(subschema, kv.Key,
                                Keyword.PropertyNames, null, kv.Key))
                            {
                                finalResult = false;
                                if (!IsRecordingErrors)
                                    return false;
                            }
                        }
                        break;

                    case Keyword.Required:
                        if (!HandleRequired(schema, map, schema.Required,
                            Keyword.Required, null))
                        {
                            finalResult = false;
                            if (!IsRecordingErrors)
                                return false;
                        }
                        break;

                    case Keyword.AdditionalProperties:
                    case Keyword.PatternProperties:
                    case Keyword.Properties:
                        propertyCheck = true;
                        break;
                }

                if (!result)
                {
                    finalResult = false;
                    if (ReportError(keyword, schema, map))
                        return false;
                }
            }

            if (propertyCheck && !ValidateProperties(map,
                    schema.Properties, schema.PatternProperties, schema.AdditionalProperties))
                finalResult = false;

            if ((schema.Flags & SchemaFlags.UnevaluatedProperties) != 0
                && (finalResult || IsRecordingErrors)
                && !ValidateUnevaluatedProperties(schema, map))
                finalResult = false;

            return finalResult;
        }

        #region Content (contentEncoding, contentMediaType)

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Checks contentEncoding and contentMediaType for a string instance. Reports errors.
        /// </summary>
        private bool CheckContent(Subschema schema, string text)
        {
            string encoding = schema.GetMetadata("contentEncoding").Value as string;
            string mediaType = schema.GetMetadata("contentMediaType").Value as string;
            if (encoding == null && mediaType == null)
                return true;

            string content = text;
            if (encoding != null)
            {
                if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
                    return true; // only base64 is checked

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(text);
                }
                catch (FormatException)
                {
                    ReportError(Keyword.ContentEncoding, schema, text, encoding);
                    return false;
                }

                try
                {
                    content = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    content = null; // binary content: only a text media type can fail below
                }
            }

            if (mediaType != null && IsJsonMediaType(mediaType)
                && (content == null || !new JsonParser { Strict = true }.TryParse(content, out _)))
            {
                ReportError(Keyword.ContentMediaType, schema, text, mediaType);
                return false;
            }

            return true;
        }

        private static bool IsJsonMediaType(string mediaType)
        {
            int semicolon = mediaType.IndexOf(';');
            string type = (semicolon >= 0 ? mediaType.Substring(0, semicolon) : mediaType).Trim();
            return string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Unevaluated properties and items (draft 2019-09)

        /// <summary>
        /// Applies unevaluatedProperties to the properties that no other keyword evaluated.
        /// </summary>
        /// <remarks>
        /// A property is evaluated if properties, patternProperties, additionalProperties or a
        /// nested unevaluatedProperties applied to it, in this schema or in any in-place
        /// subschema (allOf, anyOf, oneOf, if/then/else, $ref, dependentSchemas) that
        /// successfully validates the instance. Annotations from failing subschemas are dropped.
        /// </remarks>
        private bool ValidateUnevaluatedProperties(Subschema schema, KeyValuePair<string, object>[] map)
        {
            var evaluated = new bool[map.Length];
            MarkEvaluatedProperties(schema, map, evaluated, new HashSet<Subschema>(), isRoot: true);

            bool result = true;
            Subschema unevaluated = schema.UnevaluatedProperties;
            for (int i = 0; i < map.Length; i++)
            {
                if (evaluated[i])
                    continue;
                if (!Validate(unevaluated, map[i].Value, Keyword.UnevaluatedProperties, null, map[i].Key))
                {
                    result = false;
                    if (!IsRecordingErrors)
                        return false;
                }
            }
            return result;
        }

        private void MarkEvaluatedProperties(Subschema schema, KeyValuePair<string, object>[] map,
            bool[] evaluated, HashSet<Subschema> visiting, bool isRoot)
        {
            var flags = schema.Flags;

            // additionalProperties (and a nested unevaluatedProperties) evaluate every remaining property
            if ((flags & SchemaFlags.AdditionalProperties) != 0
                || (!isRoot && (flags & SchemaFlags.UnevaluatedProperties) != 0))
            {
                for (int i = 0; i < evaluated.Length; i++)
                    evaluated[i] = true;
                return;
            }

            if ((flags & SchemaFlags.Properties) != 0)
            {
                foreach (var kv in schema.Properties)
                {
                    int index = map.GetPropertyIndex(kv.Key);
                    if (index >= 0)
                        evaluated[index] = true;
                }
            }

            if ((flags & SchemaFlags.PatternProperties) != 0)
            {
                foreach (var pp in schema.PatternProperties)
                    for (int i = 0; i < map.Length; i++)
                        if (!evaluated[i] && pp.Key.Matches(map[i].Key) == SchemaRegexSuccess.Success)
                            evaluated[i] = true;
            }

            if (!visiting.Add(schema))
                return; // reference cycle
            foreach (var sub in SuccessfulInPlaceSubschemas(schema, map))
                MarkEvaluatedProperties(sub, map, evaluated, visiting, isRoot: false);
            visiting.Remove(schema);
        }

        /// <summary>
        /// Applies unevaluatedItems to the items after the last one that another keyword evaluated.
        /// </summary>
        private bool ValidateUnevaluatedItems(Subschema schema, object[] array)
        {
            int evaluated = EvaluatedItemCount(schema, array, new HashSet<Subschema>(), isRoot: true);
            if (evaluated >= array.Length)
                return true;

            bool result = true;
            Subschema unevaluated = schema.UnevaluatedItems;
            SchemaPointer.Push(Keyword.UnevaluatedItems);
            for (int i = evaluated; i < array.Length; i++)
            {
                if (!Validate(unevaluated, array[i], -1, i))
                {
                    result = false;
                    if (!IsRecordingErrors)
                        break;
                }
            }
            SchemaPointer.Pop();
            return result;
        }

        /// <summary>
        /// Number of leading items evaluated by items, additionalItems or a nested
        /// unevaluatedItems (int.MaxValue for all), including successful in-place subschemas.
        /// In 2019-09, contains does not count.
        /// </summary>
        private int EvaluatedItemCount(Subschema schema, object[] array, HashSet<Subschema> visiting, bool isRoot)
        {
            var flags = schema.Flags;
            if (!isRoot && (flags & SchemaFlags.UnevaluatedItems) != 0)
                return int.MaxValue;

            int count = 0;
            if ((flags & SchemaFlags.Items) != 0)
            {
                if (!(schema.Items is Subschema[] tuple))
                    return int.MaxValue; // a single items schema applies to every item
                if ((flags & SchemaFlags.AdditionalItems) != 0)
                    return int.MaxValue;
                count = tuple.Length;
            }

            if (!visiting.Add(schema))
                return count; // reference cycle
            foreach (var sub in SuccessfulInPlaceSubschemas(schema, array))
            {
                count = Math.Max(count, EvaluatedItemCount(sub, array, visiting, isRoot: false));
                if (count == int.MaxValue)
                    break;
            }
            visiting.Remove(schema);
            return count;
        }

        /// <summary>
        /// In-place subschemas of <paramref name="schema"/> that apply to the same instance and
        /// validate it, so that their annotations (evaluated properties and items) count.
        /// </summary>
        private IEnumerable<Subschema> SuccessfulInPlaceSubschemas(Subschema schema, object instance)
        {
            var flags = schema.Flags;

            if ((flags & SchemaFlags.AllOf) != 0)
                foreach (var sub in schema.AllOf)
                    if (IsValidQuietly(sub, instance))
                        yield return sub;

            if ((flags & SchemaFlags.AnyOf) != 0)
                foreach (var sub in schema.AnyOf)
                    if (IsValidQuietly(sub, instance))
                        yield return sub;

            if ((flags & SchemaFlags.OneOf) != 0)
                foreach (var sub in schema.OneOf)
                    if (IsValidQuietly(sub, instance))
                        yield return sub;

            if ((flags & SchemaFlags.If) != 0)
            {
                if (IsValidQuietly(schema.If, instance))
                {
                    yield return schema.If;
                    if (schema.Then != null && IsValidQuietly(schema.Then, instance))
                        yield return schema.Then;
                }
                else if (schema.Else != null && IsValidQuietly(schema.Else, instance))
                {
                    yield return schema.Else;
                }
            }

            if ((flags & SchemaFlags._Ref) != 0)
            {
                var r = schema.Ref;
                if (r.Version == 0 && r.Schema != null && IsValidQuietly(r.Schema, instance))
                    yield return r.Schema;
            }

            if ((flags & SchemaFlags.DependentSchemas) != 0
                && instance is KeyValuePair<string, object>[] map)
            {
                foreach (var kv in schema.DependentSchemas)
                    if (map.GetPropertyIndex(kv.Key) >= 0 && IsValidQuietly(kv.Value, instance))
                        yield return kv.Value;
            }
        }

        private bool IsValidQuietly(Subschema schema, object instance)
        {
            int level = TurnOffErrors();
            bool result = Validate(schema, instance);
            RestoreSuppressionLevel(level);
            return result;
        }

        #endregion

        /// <summary>
        /// Fast approach to search all properties
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instMap"></param>
        /// <param name="schemaMap"></param>
        /// <remarks> O(n lg(m/n)) where n is |schema properties| and m is |instance properties| </remarks>
        private void DivideAndConquer<T>(KeyValuePair<string, object>[] instMap, KeyValuePair<string, T>[] schemaMap)
            => DivideAndConquer(instMap, schemaMap, 0, instMap.Length - 1, 0, schemaMap.Length - 1);

        private void DivideAndConquer<T>(KeyValuePair<string, object>[] instMap, KeyValuePair<string, T>[] schemaMap,
            int istart, int iend,
            int sstart, int send)
        {
            while (sstart < send)
            {
                int mid = sstart + send >> 1;
                int partition = instMap.GetPropertyIndex(schemaMap[mid].Key, istart, iend);
                int istart2;
                if (partition >= 0)
                {
                    istart2 = partition + 1;
                }
                else
                {
                    partition = ~partition;
                    istart2 = partition;
                }

                DivideAndConquer(instMap, schemaMap, istart2, iend, mid + 1, send);
                iend = partition - 1;
                send = mid - 1;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="map"></param>
        /// <param name="validator"></param>
        /// <param name="properties"></param>
        /// <param name="patternProperties"></param>
        /// <param name="additionalProperties"></param>
        /// <param name="recordErrors"></param>
        /// <returns></returns>
        private bool ValidateProperties(
            KeyValuePair<string, object>[] map,
            KeyValuePair<string, Subschema>[] properties,
            KeyValuePair<SchemaRegex, Subschema>[] patternProperties,
            Subschema additionalProperties)
        {
            if (properties == null)
                properties = Array.Empty<KeyValuePair<string, Subschema>>();

            bool result = true;
            for (int i = 0, iprop = 0; i < map.Length; i++)
            {
                string prop = map[i].Key;
                object value = map[i].Value;

                // Match property
                int cmp = 1;
                bool found = false;
                for (; iprop < properties.Length; iprop++)
                {
                    cmp = string.CompareOrdinal(properties[iprop].Key, prop);
                    if (cmp >= 0)
                        break;
                }

                if (cmp == 0)
                {
                    found = true;
                    if (!Validate(properties[iprop].Value, value,
                        Keyword.Properties, prop, prop))
                    {
                        result = false;
                        if (!IsRecordingErrors)
                            return false;
                    }
                }

                // Match patternProperty
                if (patternProperties != null)
                {
                    foreach (var pp in patternProperties)
                    {
                        if (pp.Key.Matches(prop) == SchemaRegexSuccess.Success)
                        {
                            found = true;
                            if (!Validate(pp.Value, value,
                                Keyword.PatternProperties, pp.Key.Pattern, prop))
                            {
                                result = false;
                                if (!IsRecordingErrors)
                                    return false;
                            }
                        }
                    }
                }

                // Match additionalProperties
                if (found == false && additionalProperties != null)
                {
                    if (!Validate(additionalProperties, value,
                        Keyword.AdditionalProperties, null, prop))
                    {
                        result = false;
                        if (!IsRecordingErrors)
                            return false;
                    }
                }
            }

            return result;
        }

        private bool HandleRequired(Subschema schema, KeyValuePair<string, object>[] map, string[] required,
            Keyword keyword, string prop)
        {
            bool result = true;
            SchemaPointer.Push(keyword);
            if (prop != null) SchemaPointer.Push(prop);
            for (int index = 0; index < required.Length; index++)
            {
                string s = required[index];
                if (s == null || map.GetPropertyIndex(s) < 0)
                {
                    SchemaPointer.Push(index);
                    ReportError(Keyword.None, schema, actual: (object)prop ?? DBNull.Value, expected: s, instance: map);
                    SchemaPointer.Pop();

                    result = false;
                    if (!IsRecordingErrors)
                        break;
                }
            }
            if (prop != null) SchemaPointer.Pop();
            SchemaPointer.Pop();
            return result;
        }

        private bool ValidateArray(Subschema schema, object[] array)
        {
            bool finalResult = true;

            for (var mask = schema.Flags & (SchemaFlags.ArrayProperties & SchemaFlags.AllProperties);
                    mask != 0;
                    mask = (SchemaFlags)RemoveLowestBit((long)mask))
            {
                Keyword keyword = (Keyword)IndexOfLowestBit((long)mask);
                Subschema subschema;
                Subschema[] schemaArray;
                bool result = true;
                bool respond = true;
                switch (keyword)
                {
                    case Keyword.AdditionalItems:
                        // https://json-schema.org/draft/2019-09/json-schema-core.html#rfc.section.8.2.4
                        // AdditionalItems is ignored if Items is not present 
                        subschema = schema.AdditionalItems;
                        schemaArray = schema.Items as Subschema[];
                        respond = false;
                        if (schemaArray != null && schemaArray.Length < array.Length)
                        {
                            SchemaPointer.Push(Keyword.AdditionalItems);
                            for (int i = schemaArray.Length; i < array.Length; i++)
                            {
                                result &= Validate(subschema, array[i], -1, i);
                                if (!result && !IsRecordingErrors)
                                    break;
                            }
                            SchemaPointer.Pop();
                        }
                        break;

                    case Keyword.Contains:
                        subschema = schema.Contains;
                        double minContains = schema.MinContains;
                        double maxContains = schema.MaxContains;
                        result = minContains == 0;
                        int contains = 0;
                        SchemaPointer.Push(Keyword.Contains);
                        TurnOffErrors();
                        for (int i = 0; i < array.Length; i++)
                        {
                            bool elementResult = Validate(subschema, array[i], -1, i);
                            if (elementResult)
                            {
                                contains++;
                                if (contains >= minContains)
                                {
                                    result = true;
                                    if (contains + (array.Length - i - 1) <= maxContains)
                                        break;
                                }
                            }
                        }
                        TurnOnErrors();
                        SchemaPointer.Pop();
                        if (contains > maxContains)
                            result = false;
                        break;

                    case Keyword.Items:
                        object items = schema.Items;
                        SchemaPointer.Push(Keyword.Items);
                        respond = false;
                        if ((subschema = items as Subschema) != null)
                        {
                            for (int i = 0; i < array.Length; i++)
                            {
                                result &= Validate(subschema, array[i], -1, i);
                                if (!result && !IsRecordingErrors)
                                    break;
                            }
                        }
                        else
                        {
                            schemaArray = (Subschema[])items;
                            result = true;
                            for (int i = Math.Min(array.Length, schemaArray.Length) - 1; i >= 0; i--)
                            {
                                result &= Validate(schemaArray[i], array[i], i, i);
                                if (!result && !IsRecordingErrors)
                                    break;
                            }
                        }
                        SchemaPointer.Pop();
                        break;

                    case Keyword.MaxItems:
                        result = array.Length <= schema.MaxItems;
                        break;

                    case Keyword.MinItems:
                        result = array.Length >= schema.MinItems;
                        break;

                    case Keyword.UniqueItems:
                        var uniques = new HashSet<MPJson>();
                        foreach (object v in array)
                            if (!uniques.Add(MPJson.From(v)))
                            {
                                result = false;
                                break;
                            }
                        break;
                }

                if (!result)
                {
                    finalResult = false;
                    if (respond
                        ? ReportError(keyword, schema, array)
                        : !IsRecordingErrors)
                        return false;
                }
            }

            if ((schema.Flags & SchemaFlags.UnevaluatedItems) != 0
                && (finalResult || IsRecordingErrors)
                && !ValidateUnevaluatedItems(schema, array))
                finalResult = false;

            return finalResult;
        }

        private bool ValidateString(Subschema schema, string value)
        {
            bool finalResult = true;

            for (var mask = schema.Flags & SchemaFlags.StringProperties;
                    mask != 0;
                    mask = (SchemaFlags)RemoveLowestBit((long)mask))
            {
                Keyword keyword = (Keyword)IndexOfLowestBit((long)mask);
                bool? result = true;
                switch (keyword)
                {
                    // JSON Schema measures string length in Unicode code points.
                    // A string has at most as many code points as UTF-16 units, so the
                    // UTF-16 length settles most cases without counting.
                    case Keyword.MinLength:
                        result = value.Length >= schema.MinLength
                            && CodePointLength(value) >= schema.MinLength;
                        break;

                    case Keyword.MaxLength:
                        result = value.Length <= schema.MaxLength
                            || CodePointLength(value) <= schema.MaxLength;
                        break;

                    case Keyword.Pattern:
                        SchemaRegex pattern = schema.Pattern;
                        var resultType = pattern.Matches(value);
                        if (resultType != SchemaRegexSuccess.Success)
                        {
                            if (resultType == SchemaRegexSuccess.TimedOut)
                            {
                                SchemaPointer.Push(Keyword.Pattern);
                                ReportError(ErrorType.TimeLimit, schema, value);
                                SchemaPointer.Pop();
                                return false;
                            }

                            finalResult = false;
                            if (ReportError(keyword, schema, value, pattern.Pattern))
                                return false;
                        }
                        continue;

                    case Keyword.Format:
                        result = Formats.IsValueOfFormat(value, schema.Format);
                        break;
                }

                if (result == false)
                {
                    finalResult = false;
                    if (ReportError(keyword, schema, value))
                        return false;
                }
            }

            return finalResult;
        }

        private bool ValidateNumber(Subschema schema, double value)
        {
            bool finalResult = true;

            for (var mask = schema.Flags & SchemaFlags.NumberProperties;
                    mask != 0;
                    mask = (SchemaFlags)RemoveLowestBit((long)mask))
            {
                Keyword keyword = (Keyword)IndexOfLowestBit((long)mask);
                bool? result = true;
                switch (keyword)
                {
                    case Keyword.Minimum:
                        result = value >= schema.Minimum;
                        break;

                    case Keyword.Maximum:
                        result = value <= schema.Maximum;
                        break;

                    case Keyword.ExclusiveMaximum:
                        result = value < schema.ExclusiveMaximum;
                        break;

                    case Keyword.ExclusiveMinimum:
                        result = value > schema.ExclusiveMinimum;
                        break;

                    case Keyword.MultipleOf:
                        result = IsMultipleOf(value, (double)schema.MultipleOf);
                        break;
                }

                if (result == false)
                {
                    finalResult = false;
                    if (ReportError(keyword, schema, value))
                        return false;
                }
            }

            return finalResult;
        }

        // Numbers in this range with up to 17 significant digits convert to decimal exactly
        private const double DecimalMin = 1e-11;
        private const double DecimalMax = 1e27;

        /// <summary>
        /// Whether <paramref name="value"/> is an integer multiple of <paramref name="divisor"/>.
        /// </summary>
        /// <remarks>
        /// JSON numbers are decimal literals, so 0.3 is a multiple of 0.1 even though their
        /// binary doubles are not. When both numbers are in a range that decimal represents
        /// exactly, the check uses exact decimal arithmetic on their shortest round-trip forms.
        /// Otherwise it checks that the quotient is within a relative tolerance of an integer.
        /// </remarks>
        internal static bool IsMultipleOf(double value, double divisor)
        {
            if (value == 0)
                return true;

            double absValue = Math.Abs(value);
            double absDivisor = Math.Abs(divisor);
            if (absValue >= DecimalMin && absValue <= DecimalMax
                && absDivisor >= DecimalMin && absDivisor <= DecimalMax)
            {
                decimal v = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                decimal d = decimal.Parse(divisor.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                return v % d == 0m;
            }

            double quotient = value / divisor;
            if (double.IsInfinity(quotient))
                return value % divisor == 0; // e.g. 1e308 / 0.5 overflows
            double nearest = Math.Round(quotient);
            return nearest != 0 && Math.Abs(quotient - nearest) <= 1e-12 * Math.Abs(quotient);
        }

        #endregion
    }
}

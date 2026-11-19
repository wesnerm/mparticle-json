# Changelog

This project follows [Semantic Versioning](https://semver.org/). Packages: `MP.Json.Validation` (library) and `MP.Json.Cli` (`mpjson` command-line tool).

## Unreleased

### Fixes

- **Bounds of 0 are accepted.** `minItems`, `maxItems`, `minLength`, `maxLength`, `minProperties`, `maxProperties`, `minContains` and `maxContains` set to `0` no longer make the whole schema invalid.
- **String length counts Unicode code points**, as JSON Schema requires. Previously `minLength`/`maxLength` counted UTF-16 code units by default (an emoji counted as 2) and grapheme clusters with `JsonValidator.Strict` (`"e\u0301"` counted as 1). Both modes now give the same, correct result, and `Strict` no longer affects validation.
- **`$ref` no longer rejects valid documents because of nesting depth.** The old guard failed any `$ref` whose schema path was deeper than about twice the instance path, so `allOf`/`anyOf`/`oneOf` nested around a `$ref` near the root failed. It is replaced by detection of real reference cycles (the same `$ref` target re-entered on the same instance), which still fail with a `Ref` error.
- **Standard `$schema` URIs are recognized.** Previously only the exact forms without a trailing `#` (e.g. `http://json-schema.org/draft-07/schema`) were detected, so the usual `…/draft-07/schema#` and 2019-09's `https://json-schema.org/draft/2019-09/schema` were treated as "no draft declared". Both forms, with or without `#` and over `http` or `https`, are now recognized, including in `$ref`s to the meta-schemas. A recognized `$schema` takes precedence over the `MPSchema` constructor's `version` argument.
- **Keywords from other drafts are ignored**, as JSON Schema requires for unknown keywords. Previously they were either enforced (e.g. `const` in a draft-04 schema) or made the whole schema invalid (e.g. `id` and `const` together without `$schema`). A schema without a recognized `$schema` accepts keywords from every draft. `minContains`/`maxContains` now apply only in 2019-09, and the base URI comes from `id` in draft 4 and `$id` in later drafts. A known keyword with the wrong type for the draft (e.g. boolean `exclusiveMaximum` in draft 7) still makes the schema invalid.
- **`pattern`, `patternProperties` and `format: "regex"` use ECMA-262 semantics**, as JSON Schema requires, instead of .NET's:
  - `$` matches only at the end of the string, not before a final newline.
  - `\d`, `\w`, `\b` use ASCII digits and word characters; `\s` uses the ECMA-262 whitespace set (which includes U+00A0 and U+FEFF but not U+0085).
  - `.` matches any code point except line terminators (including `\n`), and a surrogate pair counts as one character, also for quantifiers on astral literals; `\u{…}` escapes are supported.
  - `[]` matches nothing, `[^]` matches anything, and `[` inside a class is a literal.
  - .NET-only syntax is rejected: `\A`, `\Z`, `\z`, `\G`, inline options such as `(?i)`, atomic groups, conditionals and `(?'name')` groups.
  - Unnamed groups capture again, so backreferences such as `(a)\1` work.
- **Draft 2019-09 keywords:**
  - Keywords next to `$ref` are applied in schemas declared as 2019-09 (drafts 4–7, and schemas without `$schema`, still ignore them).
  - `unevaluatedProperties` and `unevaluatedItems` are implemented. A property or item counts as evaluated when `properties`, `patternProperties`, `additionalProperties`, `items`, `additionalItems` or a nested `unevaluated*` applied to it, in the same schema or in an in-place subschema (`allOf`, `anyOf`, `oneOf`, `if`/`then`/`else`, `$ref`, `dependentSchemas`) that validates the instance. As 2019-09 specifies, `contains` does not count.
  - `$anchor` identifies a subschema by a plain-name fragment (`"$ref": "#name"`).
  - `$recursiveRef` resolves against the document (see Known limitations).
- **`multipleOf` is exact.** It uses decimal arithmetic on the numbers' shortest round-trip forms, so `0.3` is a multiple of `0.1` and `1e-15` is not a multiple of `1` (it used to be). Numbers outside the exact decimal range use a relative tolerance on the quotient.
- **Format regexes:** `ipv4` rejects leading zeros (`087.10.0.1`); `hostname` labels are 1–63 characters and do not start or end with `-`; `relative-json-pointer` rejects leading zeros. `ipv4`, `hostname`, `email`, `uuid` and `duration` no longer accept a trailing newline or non-ASCII digits.
- **`idn-hostname` and `idn-email` are validated** (previously any value passed). Host names use UTS #46 processing plus the IDNA2008 exceptions and contextual rules of RFC 5892 (disallowed characters such as U+302E and U+0640, the middle dot, Greek and Hebrew punctuation, the katakana middle dot, Arabic-Indic digits, and the zero width joiner).

### Added

- **`JsonValidator.ValidateContent`** (off by default) validates `contentEncoding` (`base64`) and `contentMediaType` (`application/json` and `+json` types) for draft 7 and later. JSON Schema defines these keywords as annotations, so they are not checked unless this is set.
- **`ErrorType` members** `UnevaluatedProperties`, `UnevaluatedItems`, `ContentEncoding` and `ContentMediaType`, and the `Keyword` members `UnevaluatedProperties` and `UnevaluatedItems`.

### Changed

- **`Keyword` numeric values** of the annotation keywords from `_Id` onward (such as `Title`, `Default` and `_Defs`) increased by 2 to make room for the new stored keywords. `ErrorType` values are unchanged.

### Known limitations

- Negated character classes such as `[^a]` match a single UTF-16 unit, so they can match half of a surrogate pair; astral characters given as `\u{…}` inside a character class are rejected.
- `$recursiveRef` resolves like `$ref` to the same URI. That is exact for a single schema document, which is all the library loads (remote references are not supported).
- Draft 4 treats `1.0` as a number but not an integer; the library parses all numbers as `double` and counts `1.0` as an integer in every draft.

## 2.0.0 — 2026-11-18

### Breaking changes

**JSON null and undefined have swapped representations in `MPJson`** (commit `13e8d4a`).

| Value | 1.x `Value` | 2.0 `Value` |
|---|---|---|
| JSON null (`MPJson.Null`) | `DBNull.Value` | `null` |
| Undefined (`MPJson.Undefined`) | `null` | `DBNull.Value` |

`HasValue` and `IsUndefined` behave the same as before (`MPJson.Null.HasValue` is `true`, `MPJson.Undefined.HasValue` is `false`). What changes:

- **`default(MPJson)` is now a JSON null**, not undefined. Code that used `default` to mean "no value" must use `MPJson.Undefined`.
- **`MPJson.From(null)` is now a JSON null**, not undefined. Use `MPJson.From(DBNull.Value)` or `MPJson.Undefined` for undefined.
- **Raw values:** code that inspects `MPJson.Value` or the raw object trees (`object[]`, `KeyValuePair<string, object>[]`) sees `null` for a JSON null and `DBNull.Value` for undefined.
- **`MPJson.MapAll` / `MPJson.MapChildren`** abort when the callback returns `DBNull.Value` (previously `null`). A `null` result is now an ordinary JSON null value.
- **`JsonPointer.Find`** returns `DBNull.Value` when the path does not exist (previously `null`) and `null` when the path points at a JSON null.
- **`ValidationArgs.Actual`** is undefined when an error has no offending value (for example `required`); previously it was a JSON null.

**The library targets `net10.0`** (previously `netstandard2.1`). It can no longer be used from .NET Core 3.x, .NET 5–9 or .NET Framework.

### Fixes

- **Null vs. missing:** a property whose value is `null` is no longer treated as missing by the `MPJson` indexers, `JsonPointer.Find` or `Subschema.GetMetadata`. `"default": null` is now kept as schema metadata.
- **Type errors on a null instance** now report the null instance rather than the type name.
- **Regex timeouts:** `pattern` / `patternProperties` regexes use a 100 ms timeout (was 0.5 ms). Short timeouts fired spuriously on Windows because .NET measures them with a ~15.6 ms clock. A timeout now affects only that match; previously one timeout permanently broke the pattern for the whole process.
- **Culture:** JSON numbers are parsed and formatted with the invariant culture. Previously a machine with a `,` decimal separator (e.g. de-DE) misread `1.5` and `MPJson.ToString()` wrote invalid JSON such as `1,5`.

### Added

- **`mpjson` command-line validator** (`MP.Json.Cli`): validates a JSON file or standard input against a schema and writes the errors as a JSON array. See the wiki's Command-Line Validator page.

### Maintenance

- Test project updated to `Microsoft.NET.Test.Sdk` 18.12.0, xunit 2.9.3, `xunit.runner.visualstudio` 4.0.1 and `coverlet.collector` 10.1.1, removing a vulnerable transitive `Newtonsoft.Json` 9.0.1.
- Removed the `System.Runtime.CompilerServices.Unsafe` package reference (built into .NET 10).

## 1.0.0

Initial release of MP.Json.Validation.

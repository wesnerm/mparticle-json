# Changelog

This project follows [Semantic Versioning](https://semver.org/). Packages: `MP.Json.Validation` (library) and `MP.Json.Cli` (`mpjson` command-line tool).

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

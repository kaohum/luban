# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build the solution
cd src
dotnet build Luban.sln

# Build release
dotnet build Luban.sln -c Release

# Run Luban
dotnet run --project src/Luban -- --conf <config_file> -t <target> [options]

# Key CLI options:
#   --conf       Luban config file (required)
#   -t/--target  Target name (required)
#   -c           Code generation targets (comma-separated)
#   -d           Data export targets (comma-separated)
#   -x           Extra args as key=value pairs
#   -w           Watch directories for auto-regeneration
```

No automated test suite — validation is done through the pipeline itself via built-in data validators.

## Architecture

Luban is a game configuration code/data generator. It reads schema definitions and data sources (Excel, JSON, XML, YAML, Lua, CSV), validates them, and outputs generated code (12+ languages) and serialized data (binary, JSON, etc.).

### Pipeline Flow

`Program.cs` → `DefaultPipeline` → Load Schema → Prepare `GenerationContext` → Process each target (code + data)

Generation stages: Schema collection → Type resolution → Data loading → Data validation → Code generation → Data export → Post-processing → Output save

### Core Concepts

- **Schema** (`Luban.Schema.Builtin`): Parses table/bean/enum definitions from config files into `RawDefs` → `Defs`
- **DefAssembly** (`Luban.Core/Defs/`): Fully-resolved type system — all tables, beans, enums with cross-references resolved
- **GenerationContext** (`Luban.Core/GenerationContext.cs`): Central state for a generation run; holds the assembly, tag filters, loaded table data, and L10N config
- **Type system** (`Luban.Core/Types/`): Type definitions — `TBean`, `TInt`, `TList`, etc.
- **Data types** (`Luban.Core/Datas/`): Runtime data values — `DInt`, `DString`, `DBean`, `DList`, `DDateTime`, `DDay`, etc.
- **DataLoader** (`Luban.DataLoader.Builtin`): Reads source files into `DBean` records; registered via `[DataLoader]` attribute
- **CodeTarget** (`Luban.Core/CodeTarget/`): Generates source code using Scriban templates; one project per language
- **DataTarget** (`Luban.DataTarget.Builtin`): Serializes data to output formats (binary, JSON, XML, YAML, CSV)
- **DataValidator** (`Luban.DataValidator.Builtin`): Validates ref integrity, path existence, ranges, etc.

### Type/Data Naming Conventions

| Prefix | Meaning | Example |
|---|---|---|
| `Def` | Definition (schema) | `DefBean`, `DefTable`, `DefField` |
| `T` | Type class | `TBean`, `TInt`, `TList` |
| `D` | Data instance | `DBean`, `DInt`, `DList` |
| `Raw` | Raw/unresolved definition | `RawBean`, `RawTable` |

### Plugin Registration

All loaders, validators, code targets, data targets, and pipelines self-register via attributes and are discovered at startup by their respective `*Manager` singletons:

- `[CodeTarget("name")]`
- `[DataLoader("name")]`
- `[Validator("name")]`
- `[Pipeline("name")]`

To add a new target/loader: implement the interface, apply the attribute, done.

### Visitor Pattern

The codebase uses visitors extensively for traversing types and data:

- `ITypeFuncVisitor<TResult>` / `ITypeActionVisitor` — for type traversal
- `IDataFuncVisitor<TResult>` / `IDataActionVisitor` — for data traversal

When implementing a visitor, **all types must be handled**: `TBool`, `TByte`, `TShort`, `TInt`, `TLong`, `TFloat`, `TDouble`, `TString`, `TDateTime`, `TEnum`, `TBean`, `TArray`, `TList`, `TSet`, `TMap` (and corresponding `D*` variants for data visitors).

### Template System

Code generation uses [Scriban](https://github.com/scriban/scriban) (`.sbn` files). Templates live in `{ProjectName}/Templates/{target-name}/`. Common templates: `bean.sbn`, `enum.sbn`, `table.sbn`, `tables.sbn`. Custom template functions go in a `TemplateExtension` subclass.

### Key Projects

| Project | Role |
|---|---|
| `Luban` | CLI entry point |
| `Luban.Core` | All interfaces, base classes, pipeline, type system |
| `Luban.Schema.Builtin` | Schema file parsing |
| `Luban.DataLoader.Builtin` | Excel/JSON/XML/YAML/Lua/CSV readers |
| `Luban.DataTarget.Builtin` | Binary/JSON/XML/YAML/CSV writers |
| `Luban.DataValidator.Builtin` | Ref, path, range validators |
| `Luban.<Lang>` | Per-language code generators (CSharp, Java, Golang, Python, Lua, Typescript, Cpp, Rust, ...) |
| `Luban.L10N` | Localization split export |

## C# Coding Conventions

- **Naming**: `PascalCase` for classes/methods/properties; `_camelCase` for private fields; `camelCase` for locals/params; `I` prefix for interfaces
- **Namespaces**: Root `Luban`, sub-projects `Luban.{ProjectName}`; use traditional `namespace { }` blocks (not file-scoped)
- **Nullable**: Project uses `Nullable disable` — do not use nullable reference types
- **Logging**: `private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();` (NLog)
- **Style**: 4-space indent, Allman braces, `using` statements at top sorted alphabetically
- **File header**: MIT license header (see `.cursor/rules/normal.mdc` for full text)

## Dependencies

- **NLog** 5.3.4 — logging
- **Scriban** 5.12.0 — template engine
- **CommandLineParser** 2.9.1 — CLI argument parsing

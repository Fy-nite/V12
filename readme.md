# V12

V12 is a programmable world runtime designed for building
self-modifying virtual worlds.

It combines a component-based world model with a dynamic
behavior system powered by ObjectIR, allowing worlds to
generate new behaviors, tools, and systems at runtime.

## Weird Things You Can Build With V12

With V12, you can build:
• a VR world that generates new tools on the fly

• a simulation that evolves its own logic

• a game that compiles new behaviors from node graphs

• a world that modifies its own engine-level systems

it's something that can be used to build a lot of different things.


## Current Status

V12 is an experimental open-source runtime.

The architecture is actively evolving.

Expect breaking changes while the core systems
are being refined.

## Philosophy

V12 explores a simple idea:

A virtual world should be able to modify itself.

In V12, worlds are composed of hierarchical elements
that host components. Components define behavior,
systems, and tools that interact with the world.

Because behaviors are powered by ObjectIR, developers
can generate and modify engine-level logic at runtime.

This allows worlds to:

• create tools that build new systems
• generate behaviors dynamically
• extend engine functionality
• experiment with evolving simulation logic

The goal is not just to run a game.

The goal is to run a **living programmable universe**.

## Core Concepts

### World Elements

World elements form a hierarchical structure that
represents everything inside a world.

Each element can have:

• a name
• a description
• a parent element
• attached components

This creates a flexible tree structure similar to
scene graphs used in many engines.

### Components

Components attach behavior to world elements.

A component can:

• modify its host element
• interact with other components
• spawn new elements
• attach additional components
• generate behavior dynamically

### Lifecycle

Components follow a lifecycle:

OnAttach → OnUpdate → OnDetach → OnDestroy

This allows systems to react to structural changes
in the world.

## ObjectIR Integration

V12 uses ObjectIR as a runtime behavior representation.

ObjectIR allows behaviors to be generated dynamically,
compiled, or interpreted during execution.

This makes it possible for worlds to:

• generate new behaviors
• build visual scripting tools
• implement engine-level mods
• compile runtime logic from node graphs

The long-term vision is for developers to construct
tools inside the world that generate ObjectIR programs.

## Vision

The long-term goal of V12 is to enable worlds that
can modify and extend their own behavior.

Imagine a VR environment where users can:

• build tools that create systems
• design node graphs that compile into behaviors
• generate components dynamically
• rewrite networking or simulation logic
• evolve new behaviors inside the world

In V12, the world is not just content.

The world is also the development environment.


## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Contributions are accepted under the
AGPL, with a relicensing grant to Finite so that the commercial exception can
keep working alongside it.

## License

V12 is AGPL-3.0 with a [Commercial Exception](COMMERCIAL-EXCEPTION.md).

**TL;DR:**
- Under **$1,000 USD net revenue per year**, use V12 for free. No contact needed.
- From **$1,000**, take a license if you want to ship without publishing your source. That is what the fee buys: the AGPL's source obligations, waived for your game.
- One license per studio, covering all its titles. Bands move up immediately and are billed pro-rata from the day you cross.

| Net revenue / year | Fee | Closed source |
| --- | --- | --- |
| under $1,000 | $0 | no, AGPL applies |
| $1,000 to $10,000 | $200 | yes |
| $10,000 to $100,000 | $500 | yes |
| over $100,000 | 1%, minimum $2,000 | yes |

"Net revenue" excludes refunds, storefront cuts, payment fees, and sales tax. It
counts what you actually keep.

If you would rather publish your source and comply with the AGPL, you owe
nothing at any revenue level. See [COMMERCIAL-EXCEPTION.md](COMMERCIAL-EXCEPTION.md)
for definitions, revenue rules, crossing and renewal mechanics, and the full
closed-source grant.

`Contract` and the `ObjektRT` libraries are MIT licensed and are not covered by
the exception, because they do not need one.

We trust you to be honest, and we're happy to work with successful creators.

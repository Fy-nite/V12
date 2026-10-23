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


## License

V12 is AGPL-3.0 with a Commercial Exception.

**TL;DR:**
- If your V12‑based project earns **less than $1,000 USD per year** (from any source: sales, microtransactions, ads, donations, etc.), you may use V12 freely without contacting us.
- If your project earns **$1,000 or more per year**, you need a commercial license – contact us for affordable terms (e.g., $200/year for the $1k–$10k tier).
- See [COMMERCIAL-EXCEPTION.md](COMMERCIAL-EXCEPTION.md) for full details.

We trust you to be honest, and we're happy to work with successful creators.

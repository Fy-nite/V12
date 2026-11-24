using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace V12.MCP.Tools;

/// <summary>Read-only tools. Safe to use with a probe running in read-only mode.</summary>
[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(Name = "probe_status"), Description(
        "Check that the running game and its probe are reachable. Returns pid, port, " +
        "read-only flag, world count and the current simulation tick.")]
    public static async Task<string> ProbeStatus(CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync("v1/health", cancellationToken));

    [McpServerTool(Name = "list_worlds"), Description(
        "List the worlds loaded in the running game: id, name, authority, simulated/visible " +
        "state and element count. Start here for a world id.")]
    public static async Task<string> ListWorlds(CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync("v1/worlds", cancellationToken));

    [McpServerTool(Name = "list_modules"), Description(
        "List the modules the game considered loading: name, version, assembly, start state " +
        "and why anything was rejected (missing dependency, cycle, declined).")]
    public static async Task<string> ListModules(CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync("v1/modules", cancellationToken));

    [McpServerTool(Name = "list_mods"), Description(
        "List the mod paks the game considered loading: id, version, author, whether it " +
        "loaded, and why not (disabled, API mismatch, missing dependency).")]
    public static async Task<string> ListMods(CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync("v1/mods", cancellationToken));

    [McpServerTool(Name = "get_tree"), Description(
        "Read the live scene tree as indented text: '#id Name (n comp) [inactive]'. " +
        "Filter by depth, name or component type to keep the result small.")]
    public static async Task<string> GetTree(
        [Description("Maximum element depth from the world root. 0 returns roots only.")]
        int depth = 2,
        [Description("Maximum number of elements to return.")]
        int limit = 300,
        [Description("Only elements whose name contains this text (case-insensitive).")]
        string? name = null,
        [Description("Only elements carrying a component of this type name.")]
        string? component = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("v1/snapshot?components=false")
            .Append("&depth=").Append(Math.Max(0, depth))
            .Append("&limit=").Append(Math.Clamp(limit, 1, 5000));

        AppendFilter(query, "name", name);
        AppendFilter(query, "component", component);

        var snapshot = await ProbeClient.Current.GetAsync(query.ToString(), cancellationToken);
        return SceneText.Tree(snapshot);
    }

    [McpServerTool(Name = "get_element"), Description(
        "Full state of one element: transform, active flag and every component with its " +
        "properties. Use the id from get_tree or find_elements.")]
    public static async Task<string> GetElement(
        [Description("Element id.")]
        long id,
        [Description("Include component property bags. Turn off for a cheap transform-only read.")]
        bool includeComponents = true,
        CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync(
            $"v1/element/{id}?components={(includeComponents ? "true" : "false")}",
            cancellationToken));

    [McpServerTool(Name = "find_elements"), Description(
        "Search loaded elements by name, component type and/or active state. Returns one " +
        "compact line per match with position and component types.")]
    public static async Task<string> FindElements(
        [Description("Name substring (case-insensitive).")]
        string? name = null,
        [Description("Component type name the element must carry.")]
        string? component = null,
        [Description("Filter by active state. Omit for either.")]
        bool? active = null,
        [Description("Maximum matches to return.")]
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("v1/snapshot?components=true")
            .Append("&limit=").Append(Math.Clamp(limit, 1, 1000));

        AppendFilter(query, "name", name);
        AppendFilter(query, "component", component);

        if (active is { } flag)
            query.Append("&active=").Append(flag ? "true" : "false");

        var snapshot = await ProbeClient.Current.GetAsync(query.ToString(), cancellationToken);
        return SceneText.List(snapshot, Math.Clamp(limit, 1, 1000));
    }

    [McpServerTool(Name = "get_journal"), Description(
        "Recent scene commands applied by the probe, oldest first. This is the audit log " +
        "and, on the fixed-step clock, a replay script.")]
    public static async Task<string> GetJournal(
        [Description("How many recent entries to return.")]
        int take = 50,
        CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync(
            $"v1/journal?take={Math.Clamp(take, 1, 1024)}",
            cancellationToken));

    [McpServerTool(Name = "get_receipt"), Description(
        "Look up the receipt for a command request id: accepted/error, applied tick, " +
        "previous value and the id of anything created.")]
    public static async Task<string> GetReceipt(
        [Description("Request id returned by a write tool.")]
        string requestId,
        CancellationToken cancellationToken = default)
        => Wire.Pretty(await ProbeClient.Current.GetAsync(
            "v1/receipts/" + Uri.EscapeDataString(requestId),
            cancellationToken));

    private static void AppendFilter(StringBuilder query, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }
}

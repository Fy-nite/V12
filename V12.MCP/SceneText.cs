using System.Text;
using System.Text.Json;

namespace V12.MCP;

/// <summary>
/// Turns probe snapshots into compact text. Raw snapshot JSON is precise but
/// expensive to read; a tree is what a model actually needs to navigate, and
/// <c>get_element</c> is there when it needs the full property bag.
/// </summary>
internal static class SceneText
{
    public static string Tree(JsonElement snapshot)
    {
        var builder = new StringBuilder();
        var worlds = snapshot.GetProperty("worlds").EnumerateArray().ToList();
        var elements = snapshot.GetProperty("elements").EnumerateArray().ToList();

        foreach (var world in worlds)
        {
            string worldId = Wire.String(world, "id") ?? "";
            string name = Wire.String(world, "name") ?? "(world)";
            string authority = Wire.String(world, "authority") ?? "";

            var inWorld = elements.Where(e => Wire.String(e, "worldId") == worldId).ToList();

            builder.Append("world ").Append(name)
                   .Append("  id=").Append(worldId)
                   .Append("  authority=").Append(authority)
                   .Append("  elements=").Append(inWorld.Count)
                   .AppendLine();

            if (inWorld.Count == 0)
            {
                builder.AppendLine("  (empty)");
                continue;
            }

            var byParent = inWorld
                .GroupBy(e => Wire.Long(e, "parentId"))
                .ToDictionary(g => g.Key, g => g.OrderBy(e => Wire.Long(e, "id")).ToList());

            var visited = new HashSet<long>();

            void Walk(long parent, int depth)
            {
                if (!byParent.TryGetValue(parent, out var children)) return;

                foreach (var element in children)
                {
                    long id = Wire.Long(element, "id");
                    if (!visited.Add(id)) continue;

                    AppendElement(builder, element, depth);
                    Walk(id, depth + 1);
                }
            }

            Walk(0, 1);

            // Depth/limit filters can orphan a node whose parent was cut. Show
            // it at its real depth instead of dropping it.
            foreach (var orphan in inWorld.Where(e => !visited.Contains(Wire.Long(e, "id"))))
                AppendElement(builder, orphan, (int)Wire.Long(orphan, "depth", 1));
        }

        return builder.ToString();
    }

    public static string List(JsonElement snapshot, int limit)
    {
        var builder = new StringBuilder();

        var worldNames = snapshot.GetProperty("worlds").EnumerateArray()
            .ToDictionary(
                w => Wire.String(w, "id") ?? "",
                w => Wire.String(w, "name") ?? "");

        int shown = 0;
        foreach (var element in snapshot.GetProperty("elements").EnumerateArray())
        {
            if (shown >= limit) break;
            shown++;

            string worldId = Wire.String(element, "worldId") ?? "";
            worldNames.TryGetValue(worldId, out var worldName);

            builder.Append('#').Append(Wire.Long(element, "id")).Append(' ')
                   .Append(Wire.String(element, "name") ?? "(unnamed)")
                   .Append("  world=").Append(worldName ?? worldId)
                   .Append("  pos=").Append(Position(element))
                   .Append("  global=").Append(GlobalPosition(element));

            string components = ComponentTypes(element);
            if (components.Length > 0)
                builder.Append("  components=").Append(components);

            if (!element.TryGetProperty("active", out var active) || !active.GetBoolean())
                builder.Append("  [inactive]");

            builder.AppendLine();
        }

        if (shown == 0)
            builder.AppendLine("(no matching elements)");

        return builder.ToString();
    }

    private static void AppendElement(StringBuilder builder, JsonElement element, int depth)
    {
        builder.Append(new string(' ', Math.Clamp(depth, 0, 40) * 2))
               .Append('#').Append(Wire.Long(element, "id"));

        string? name = Wire.String(element, "name");
        if (!string.IsNullOrEmpty(name))
            builder.Append(' ').Append(name);

        int components = element.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength()
            : 0;

        if (components > 0)
            builder.Append(" (").Append(components).Append(" comp)");

        if (!element.TryGetProperty("active", out var active) || !active.GetBoolean())
            builder.Append(" [inactive]");

        builder.AppendLine();
    }

    private static string Position(JsonElement element)
    {
        if (!element.TryGetProperty("local", out var local) ||
            !local.TryGetProperty("position", out var position))
            return "?";

        return $"{Wire.Number(position, "x"):0.###},{Wire.Number(position, "y"):0.###},{Wire.Number(position, "z"):0.###}";
    }

    private static string GlobalPosition(JsonElement element)
    {
        if (!element.TryGetProperty("globalPosition", out var global) ||
            global.ValueKind != JsonValueKind.Object)
            return "?";

        return $"{Wire.Number(global, "x"):0.###},{Wire.Number(global, "y"):0.###},{Wire.Number(global, "z"):0.###}";
    }

    private static string ComponentTypes(JsonElement element)
    {
        if (!element.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join(", ", components.EnumerateArray()
            .Select(c => Wire.String(c, "type"))
            .Where(t => !string.IsNullOrEmpty(t)));
    }
}

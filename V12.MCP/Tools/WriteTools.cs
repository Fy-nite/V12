using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace V12.MCP.Tools;

/// <summary>
/// Write tools. Each one queues a single <c>SceneCommand</c>; the game applies it
/// at its next tick, so the tree is never mutated from this process.
/// </summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = "set_transform"), Description(
        "Move, rotate or scale an element. Omitted components keep their current value. " +
        "Rotation is a quaternion (x,y,z,w); read it first with get_element if you need to change it.")]
    public static async Task<string> SetTransform(
        [Description("Element id.")]
        long targetId,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        double? positionX = null,
        double? positionY = null,
        double? positionZ = null,
        double? rotationX = null,
        double? rotationY = null,
        double? rotationZ = null,
        double? rotationW = null,
        double? scaleX = null,
        double? scaleY = null,
        double? scaleZ = null,
        CancellationToken cancellationToken = default)
    {
        var element = await ProbeClient.Current.GetAsync(
            $"v1/element/{targetId}?components=false",
            cancellationToken);

        if (!element.TryGetProperty("local", out var local))
            return $"element {targetId} has no local transform in the snapshot";

        var position = local.GetProperty("position");
        var rotation = local.GetProperty("rotation");
        var scale = local.GetProperty("scale");

        var command = Base("setTransform", targetId, worldId);
        command["transform"] = new
        {
            position = new
            {
                x = positionX ?? Wire.Number(position, "x"),
                y = positionY ?? Wire.Number(position, "y"),
                z = positionZ ?? Wire.Number(position, "z"),
            },
            rotation = new
            {
                x = rotationX ?? Wire.Number(rotation, "x"),
                y = rotationY ?? Wire.Number(rotation, "y"),
                z = rotationZ ?? Wire.Number(rotation, "z"),
                w = rotationW ?? Wire.Number(rotation, "w", 1),
            },
            scale = new
            {
                x = scaleX ?? Wire.Number(scale, "x", 1),
                y = scaleY ?? Wire.Number(scale, "y", 1),
                z = scaleZ ?? Wire.Number(scale, "z", 1),
            },
        };

        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "set_active"), Description(
        "Show or hide an element. Inactive elements are skipped by rendering.")]
    public static async Task<string> SetActive(
        [Description("Element id.")]
        long targetId,
        [Description("True to activate, false to hide.")]
        bool active,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        CancellationToken cancellationToken = default)
    {
        var command = Base("setActive", targetId, worldId);
        command["value"] = new { kind = "bool", flag = active };
        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "set_property"), Description(
        "Set a component property. The value is inferred: true/false, integer, float, " +
        "'x,y,z' for a Vector3, otherwise text. Returns the previous value in the receipt.")]
    public static async Task<string> SetProperty(
        [Description("Element id.")]
        long targetId,
        [Description("Component property name, e.g. Energy.")]
        string property,
        [Description("New value as text; type is inferred.")]
        string value,
        [Description("Component type name, e.g. GenericLightComponent.")]
        string? componentType = null,
        [Description("Component instance id, when the element has several of one type.")]
        long componentId = 0,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        CancellationToken cancellationToken = default)
    {
        var command = Base("setProperty", targetId, worldId);
        command["property"] = property;
        command["value"] = InferValue(value);
        if (!string.IsNullOrWhiteSpace(componentType)) command["componentType"] = componentType;
        if (componentId != 0) command["componentId"] = componentId;

        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "spawn_element"), Description(
        "Create an empty element, optionally under a parent, and return its new id in the receipt.")]
    public static async Task<string> SpawnElement(
        [Description("Name for the new element.")]
        string name,
        [Description("World id. Omit to use the focused world.")]
        string? worldId = null,
        [Description("Parent element id. 0 creates it at the world root.")]
        long parentId = 0,
        double? positionX = null,
        double? positionY = null,
        double? positionZ = null,
        CancellationToken cancellationToken = default)
    {
        var command = Base("spawn", 0, worldId);
        command["name"] = name;
        if (parentId != 0) command["parentId"] = parentId;

        if (positionX is not null || positionY is not null || positionZ is not null)
        {
            command["transform"] = new
            {
                position = new
                {
                    x = positionX ?? 0,
                    y = positionY ?? 0,
                    z = positionZ ?? 0,
                },
            };
        }

        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "despawn_element"), Description(
        "Remove an element and its subtree from the world.")]
    public static async Task<string> DespawnElement(
        [Description("Element id.")]
        long targetId,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        CancellationToken cancellationToken = default)
        => await Send(Base("despawn", targetId, worldId), cancellationToken);

    [McpServerTool(Name = "attach_component"), Description(
        "Attach a new component by type name. Returns the component id in the receipt.")]
    public static async Task<string> AttachComponent(
        [Description("Element id.")]
        long targetId,
        [Description("Component type name, e.g. MeshComponent, GenericLightComponent.")]
        string componentType,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        CancellationToken cancellationToken = default)
    {
        var command = Base("attachComponent", targetId, worldId);
        command["componentType"] = componentType;
        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "detach_component"), Description(
        "Remove a component from an element by id or type name.")]
    public static async Task<string> DetachComponent(
        [Description("Element id.")]
        long targetId,
        [Description("Component type name. Needed unless componentId is given.")]
        string? componentType = null,
        [Description("Component instance id.")]
        long componentId = 0,
        [Description("World id. Omit to search every loaded world.")]
        string? worldId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(componentType) && componentId == 0)
            return "give either componentType or componentId";

        var command = Base("detachComponent", targetId, worldId);
        if (!string.IsNullOrWhiteSpace(componentType)) command["componentType"] = componentType;
        if (componentId != 0) command["componentId"] = componentId;

        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "send_input"), Description(
        "Inject an input event into the real input service, as if the player pressed it. " +
        "Use this to drive gameplay rather than calling its functions directly.")]
    public static async Task<string> SendInput(
        [Description("InputEventType name: buttonDown, buttonUp, axis, pointer, text.")]
        string type,
        [Description("Logical action name, e.g. Jump.")]
        string? name = null,
        [Description("Physical key name, e.g. Space, W.")]
        string? key = null,
        [Description("Axis or analog value.")]
        float value = 0,
        [Description("Pressed state for button events.")]
        bool isPressed = true,
        CancellationToken cancellationToken = default)
    {
        var command = Base("sendInput", 0, null);
        command["input"] = new { type, name, key, value, isPressed };
        return await Send(command, cancellationToken);
    }

    [McpServerTool(Name = "set_simulation"), Description(
        "Pause/unpause the fixed-step simulation and/or change time scale. " +
        "Editing keeps working while paused.")]
    public static async Task<string> SetSimulation(
        [Description("True pauses the simulation, false resumes it. Omit to leave unchanged.")]
        bool? paused = null,
        [Description("Time scale, 0 to 64. 1 is normal. Omit to leave unchanged.")]
        double? timeScale = null,
        CancellationToken cancellationToken = default)
    {
        var commands = new List<object>();

        if (paused is { } flag)
        {
            var command = Base("pause", 0, null);
            command["value"] = new { kind = "bool", flag };
            commands.Add(command);
        }

        if (timeScale is { } scale)
        {
            var command = Base("setTimeScale", 0, null);
            command["value"] = new { kind = "float", number = scale };
            commands.Add(command);
        }

        if (commands.Count == 0)
            return "nothing to change: pass paused and/or timeScale";

        return Wire.Pretty(await ProbeClient.Current.SendCommandsAsync(commands, cancellationToken));
    }

    private static async Task<string> Send(Dictionary<string, object?> command, CancellationToken cancellationToken)
        => Wire.Pretty(await ProbeClient.Current.SendCommandAsync(command, cancellationToken));

    private static Dictionary<string, object?> Base(string op, long targetId, string? worldId)
    {
        var command = new Dictionary<string, object?>
        {
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["op"] = op,
            ["targetId"] = targetId,
            ["actor"] = "mcp",
        };

        if (!string.IsNullOrWhiteSpace(worldId))
            command["worldId"] = worldId;

        return command;
    }

    /// <summary>
    /// Infer a snapshot value from text. Deliberately narrow: the probe rejects
    /// anything it cannot assign, and the receipt says why.
    /// </summary>
    private static object InferValue(string text)
    {
        if (bool.TryParse(text, out bool flag))
            return new { kind = "bool", flag };

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            return new { kind = "integer", number = integer };

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return new { kind = "float", number };

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            return new { kind = "vector3", vector = new { x, y, z } };
        }

        return new { kind = "text", text };
    }
}

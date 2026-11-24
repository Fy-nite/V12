using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using V12.Core;
using V12.Core.Modules;

namespace V12.MCP.Probe
{
    /// <summary>
    /// Opt-in MCP probe. Loading this assembly is not enough to open a port: the
    /// game also has to be launched with <c>--mcp</c> (or another
    /// <c>--mcp-*</c> switch), so an accidental copy of the DLL in a shipping
    /// build stays inert.
    ///
    /// <para>
    /// Everything the probe exposes is answered through
    /// <see cref="SceneIntrospection"/>, so reads and writes hop onto the tick
    /// thread and the engine keeps owning the tree.
    /// </para>
    /// </summary>
    public sealed class ProbeModule : IV12Module
    {
        public string Name => "V12.MCP.Probe";

        private ProbeServer? _server;

        public bool Start(ModuleContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var options = ProbeOptions.Parse(context.Args ?? Array.Empty<string>());
            if (!options.Enabled)
                return false;

            var server = new ProbeServer(context.Root, options);
            if (!server.Start())
                return false;

            _server = server;
            WriteHandshakeFile(options, server);
            return true;
        }

        public void Dispose()
        {
            _server?.Dispose();
            _server = null;
        }

        /// <summary>
        /// Leaves the URL and token next to the other per-process temp files so a
        /// client can pick them up without parsing a log line. The file is
        /// per-pid, so several games can run at once.
        /// </summary>
        private static void WriteHandshakeFile(ProbeOptions options, ProbeServer server)
        {
            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "v12-mcp");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, $"probe-{Environment.ProcessId}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    url = server.Url,
                    token = options.Token,
                    readOnly = options.ReadOnly,
                    pid = Environment.ProcessId,
                    process = Process.GetCurrentProcess().ProcessName,
                }, new JsonSerializerOptions { WriteIndented = true }));

                GameRoot.Log.Information("MCP probe listening at {Url} (token file: {Path})", server.Url, path);
                Console.Error.WriteLine($"[V12.MCP.Probe] listening at {server.Url}");
                Console.Error.WriteLine($"[V12.MCP.Probe] token file: {path}");
            }
            catch (Exception ex)
            {
                GameRoot.Log.Warning(ex, "MCP probe could not write its handshake file");
            }
        }
    }
}

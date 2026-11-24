using System;
using System.Globalization;

namespace V12.MCP.Probe
{
    /// <summary>
    /// Command-line switches for the probe. The module is only started when the
    /// user asked for it; an assembly that is merely present does nothing.
    /// </summary>
    internal sealed class ProbeOptions
    {
        public const int DefaultPort = 7420;

        public bool Enabled { get; private set; }
        public string Bind { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = DefaultPort;
        public bool ReadOnly { get; private set; }
        public string Token { get; private set; } = "";
        public TimeSpan SnapshotTimeout { get; private set; } = TimeSpan.FromSeconds(2);
        public TimeSpan CommandTimeout { get; private set; } = TimeSpan.FromSeconds(5);

        public static ProbeOptions Parse(string[] args)
        {
            var options = new ProbeOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? inline = null;

                int equals = arg.IndexOf('=');
                if (equals > 0 && arg.StartsWith("--", StringComparison.Ordinal))
                {
                    inline = arg[(equals + 1)..];
                    arg = arg[..equals];
                }

                switch (arg)
                {
                    case "--mcp":
                        options.Enabled = true;
                        break;

                    case "--mcp-port":
                        options.Enabled = true;
                        if (int.TryParse(inline ?? Next(args, ref i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                            options.Port = port;
                        break;

                    case "--mcp-bind":
                        options.Enabled = true;
                        options.Bind = inline ?? Next(args, ref i) ?? options.Bind;
                        break;

                    case "--mcp-token":
                        options.Enabled = true;
                        options.Token = inline ?? Next(args, ref i) ?? options.Token;
                        break;

                    case "--mcp-readonly":
                        options.Enabled = true;
                        options.ReadOnly = true;
                        break;

                    case "--mcp-snapshot-timeout-ms":
                        if (int.TryParse(inline ?? Next(args, ref i), out int snapshotMs) && snapshotMs > 0)
                            options.SnapshotTimeout = TimeSpan.FromMilliseconds(snapshotMs);
                        break;

                    case "--mcp-command-timeout-ms":
                        if (int.TryParse(inline ?? Next(args, ref i), out int commandMs) && commandMs > 0)
                            options.CommandTimeout = TimeSpan.FromMilliseconds(commandMs);
                        break;
                }
            }

            // A token is generated, never optional: a loopback port is still
            // reachable by every process on the machine.
            if (string.IsNullOrWhiteSpace(options.Token) && options.Enabled)
                options.Token = Guid.NewGuid().ToString("N");

            return options;
        }

        private static string? Next(string[] args, ref int index)
            => index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : null;
    }
}

using System;
using System.IO;

namespace CodexProSafeManager
{
    [Serializable]
    internal sealed class AppSettings
    {
        public string RepositoryPath { get; set; }
        public string WorkspaceRoot { get; set; }
        public string AllowedRoot { get; set; }
        public string NodePath { get; set; }
        public string TunnelClientPath { get; set; }
        public string TunnelProfile { get; set; }
        public string ControlPlaneApiKey { get; set; }
        public string OrganizationId { get; set; }
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool AutoStartServices { get; set; }
        public bool RestartOnFailure { get; set; }
        public string CodexDiagnosticReadMode { get; set; }
        public string DiagnosticHelperPath { get; set; }
        public string DiagnosticHelperProtocolVersion { get; set; }
        public string DiagnosticHelperSha256 { get; set; }

        public static AppSettings CreateDefaults()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string repo = Path.Combine(user, @"Projects\github\codexpro-safe");
            string tunnel = Path.Combine(user, @"Documents\tunnel-client-v0.0.10-windows-amd64\tunnel-client.exe");

            return new AppSettings
            {
                RepositoryPath = repo,
                WorkspaceRoot = Path.Combine(user, "Projects"),
                AllowedRoot = Path.Combine(user, "Projects"),
                NodePath = FindNode(),
                TunnelClientPath = tunnel,
                TunnelProfile = "codexpro-safe-local",
                ControlPlaneApiKey = String.Empty,
                OrganizationId = String.Empty,
                StartWithWindows = false,
                StartMinimized = false,
                AutoStartServices = false,
                RestartOnFailure = true,
                CodexDiagnosticReadMode = "off",
                DiagnosticHelperPath = String.Empty,
                DiagnosticHelperProtocolVersion = String.Empty,
                DiagnosticHelperSha256 = String.Empty
            };
        }

        public void ApplyMissingDefaults()
        {
            AppSettings defaults = CreateDefaults();
            if (String.IsNullOrWhiteSpace(RepositoryPath)) RepositoryPath = defaults.RepositoryPath;
            if (String.IsNullOrWhiteSpace(WorkspaceRoot)) WorkspaceRoot = defaults.WorkspaceRoot;
            if (String.IsNullOrWhiteSpace(AllowedRoot)) AllowedRoot = defaults.AllowedRoot;
            if (String.IsNullOrWhiteSpace(NodePath)) NodePath = defaults.NodePath;
            if (String.IsNullOrWhiteSpace(TunnelClientPath)) TunnelClientPath = defaults.TunnelClientPath;
            if (String.IsNullOrWhiteSpace(TunnelProfile)) TunnelProfile = defaults.TunnelProfile;
            if (ControlPlaneApiKey == null) ControlPlaneApiKey = String.Empty;
            if (OrganizationId == null) OrganizationId = String.Empty;
            if (String.IsNullOrWhiteSpace(CodexDiagnosticReadMode)) CodexDiagnosticReadMode = "off";
            if (DiagnosticHelperPath == null) DiagnosticHelperPath = String.Empty;
            if (DiagnosticHelperProtocolVersion == null) DiagnosticHelperProtocolVersion = String.Empty;
            if (DiagnosticHelperSha256 == null) DiagnosticHelperSha256 = String.Empty;
        }

        public string ValidateForConnector()
        {
            if (!File.Exists(NodePath)) return "Node.js was not found at " + NodePath;
            if (!Directory.Exists(RepositoryPath)) return "Repository was not found at " + RepositoryPath;
            if (!File.Exists(Path.Combine(RepositoryPath, @"scripts\codexpro.mjs")))
                return "scripts\\codexpro.mjs was not found in the selected repository.";
            if (!File.Exists(Path.Combine(RepositoryPath, @"dist\http.js")))
                return "dist\\http.js is missing. Run npm.cmd run build in the repository.";
            if (!Directory.Exists(WorkspaceRoot)) return "Workspace root was not found at " + WorkspaceRoot;
            if (!Directory.Exists(AllowedRoot)) return "Allowed root was not found at " + AllowedRoot;
            if (CodexDiagnosticReadMode != "off" && CodexDiagnosticReadMode != "read")
                return "Codex diagnostic read mode must be off or read.";
            if (CodexDiagnosticReadMode == "read")
            {
                if (!File.Exists(DiagnosticHelperPath)) return "The sealed diagnostic helper is missing. Reinstall the Manager before enabling diagnostics.";
                if (DiagnosticHelperProtocolVersion != DiagnosticHelperTrust.ProtocolVersion)
                    return "The diagnostic helper protocol does not match this Manager build. Reinstall the Manager.";
                if (String.IsNullOrWhiteSpace(DiagnosticHelperSha256) || DiagnosticHelperSha256.Length != 64)
                    return "The diagnostic helper fingerprint is missing. Reinstall the Manager before enabling diagnostics.";
            }
            return null;
        }

        public string ValidateForTunnel()
        {
            if (!File.Exists(TunnelClientPath)) return "Tunnel client was not found at " + TunnelClientPath;
            if (String.IsNullOrWhiteSpace(TunnelProfile)) return "Tunnel profile is required.";
            if (String.IsNullOrWhiteSpace(ControlPlaneApiKey))
                return "The Control Plane API key has not been saved. Open Settings and enter it once.";
            return null;
        }

        private static string FindNode()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string candidate = Path.Combine(programFiles, @"nodejs\node.exe");
            if (File.Exists(candidate)) return candidate;
            return "node.exe";
        }
    }
}

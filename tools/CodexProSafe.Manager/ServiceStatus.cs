namespace CodexProSafeManager
{
    internal enum ServiceState
    {
        Stopped,
        Starting,
        RunningOwned,
        RunningExternal,
        Stopping,
        Faulted
    }

    internal sealed class ServiceSnapshot
    {
        public ServiceState ConnectorState { get; set; }
        public ServiceState TunnelState { get; set; }
        public string ConnectorDetail { get; set; }
        public string TunnelDetail { get; set; }
        public bool ConnectorHealthy { get; set; }
        public bool TunnelHealthy { get; set; }
    }
}

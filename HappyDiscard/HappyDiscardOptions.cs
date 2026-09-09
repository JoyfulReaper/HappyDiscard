using JoyfulReaperLib.TcpServer;

public sealed class HappyDiscardOptions : ITcpServerOptions
{
    public const string SectionName = "Discard";

    public string ListenAddress { get; set; } = "::";
    public bool DualMode { get; set; } = true;
    public int Port { get; set; } = 9;
    public int MaxConcurrentConnections { get; set; } = 64;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public string[] TelemetryIgnoredRemoteAddresses { get; set; } = [];
    public long MaxBytesPerConnection { get; set; } = 1_048_576;

    public bool UdpEnabled { get; set; } = false;
    public string? UdpListenAddress { get; set; }
    public int? UdpPort { get; set; }
    public int MaxUdpDatagramBytes { get; set; } = 65_507;

    ConnectionLimitBehavior ITcpServerOptions.ConnectionLimitBehavior =>
        ConnectionLimitBehavior.Wait;
}

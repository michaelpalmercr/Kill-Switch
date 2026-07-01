namespace KillSwitch;

/// <summary>A packet-capture backend (raw sockets or Npcap).</summary>
public interface IPacketCapture : IDisposable
{
    string Name { get; }

    /// <summary>Raised on every captured packet (from a background thread).</summary>
    event Action<RawPacket>? Packet;

    /// <summary>Begin capturing. Throws if the backend can't start (e.g. driver missing).</summary>
    void Start();

    void Stop();
}

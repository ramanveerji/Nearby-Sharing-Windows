﻿namespace ShortDev.Microsoft.ConnectedDevices.Protocol.Platforms;

public interface ICdpPlatformHandler
{
    void Log(int level, string message);
    string GetLocalIP();
}
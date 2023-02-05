﻿using ShortDev.Microsoft.ConnectedDevices.Messages;
using ShortDev.Microsoft.ConnectedDevices.Messages.Connection;
using ShortDev.Microsoft.ConnectedDevices.Messages.Connection.TransportUpgrade;
using ShortDev.Microsoft.ConnectedDevices.Platforms;
using ShortDev.Microsoft.ConnectedDevices.Transports;
using ShortDev.Networking;
using System;
using System.Linq;

namespace ShortDev.Microsoft.ConnectedDevices.Internal;

internal sealed class UpgradeHandler
{
    CdpSession _session;
    public UpgradeHandler(CdpSession session, CdpDevice initalDevice)
    {
        _session = session;

        // Initial address is always allowed
        _allowedAddresses.Add(initalDevice.Address);
    }

    ConcurrentList<string> _allowedAddresses = new();
    public bool IsSocketAllowed(CdpSocket socket)
        => _allowedAddresses.Contains(socket.RemoteDevice.Address);

    public bool HandleConnect(CdpSocket socket, CommonHeader header, ConnectionHeader connectionHeader, EndianReader reader)
    {
        // This part needs to be always accessible!
        // This is used to validate
        if (connectionHeader.MessageType == ConnectionType.TransportRequest)
        {
            HandleTransportRequest(socket, reader);
            return true;
        }

        // If invalid socket, return false and let CdpSession.HandleConnect throw
        if (!IsSocketAllowed(socket))
            return false;

        switch (connectionHeader.MessageType)
        {
            case ConnectionType.UpgradeRequest:
                HandleUpgradeRequest(socket, reader);
                return true;
            case ConnectionType.UpgradeFinalization:
                HandleUpgradeFinalization(socket, reader);
                return true;
            case ConnectionType.UpgradeFailure:
                HandleUpgradeFailure(reader);
                return true;
        }
        return false;
    }

    readonly ConcurrentList<Guid> _upgradeIds = new();
    void HandleTransportRequest(CdpSocket socket, EndianReader reader)
    {
        var msg = TransportRequest.Parse(reader);

        // Sometimes the device sends multiple transport requests
        // If we know it already then let it pass
        bool allowed = IsSocketAllowed(socket);
        if (!allowed && _upgradeIds.Contains(msg.UpgradeId))
        {
            // No we have confirmed that this address belongs to the same device (different transport)
            _allowedAddresses.Add(socket.RemoteDevice.Address);
            _upgradeIds.Remove(msg.UpgradeId);

            allowed = true;
        }

        _session.Platform.Handler.Log(0, $"Transport upgrade {msg.UpgradeId} {(allowed ? "succeeded" : "failed")}");

        CommonHeader header = new()
        {
            Type = MessageType.Connect
        };

        _session.SendMessage(socket, header, (writer) =>
        {
            new ConnectionHeader()
            {
                ConnectionMode = ConnectionMode.Proximal,
                MessageType = allowed ? ConnectionType.TransportConfirmation : ConnectionType.UpgradeFailure
            }.Write(writer);
            msg.Write(writer);
        });
    }

    void HandleUpgradeRequest(CdpSocket socket, EndianReader reader)
    {
        var msg = UpgradeRequest.Parse(reader);
        _session.Platform.Handler.Log(0, $"Upgrade request {msg.UpgradeId} to {string.Join(',', msg.Endpoints.Select((x) => x.Type.ToString()))}");

        CommonHeader header = new()
        {
            Type = MessageType.Connect
        };

        var networkTransport = _session.Platform.TryGetTransport<NetworkTransport>();
        if (networkTransport == null)
        {
            _session.SendMessage(socket, header, (writer) =>
            {
                new ConnectionHeader()
                {
                    ConnectionMode = ConnectionMode.Proximal,
                    MessageType = ConnectionType.UpgradeFailure
                }.Write(writer);
                new HResultPayload()
                {
                    HResult = -1
                }.Write(writer);
            });

            return;
        }

        _upgradeIds.Add(msg.UpgradeId);

        _session.SendMessage(socket, header, (writer) =>
        {
            new ConnectionHeader()
            {
                ConnectionMode = ConnectionMode.Proximal,
                MessageType = ConnectionType.UpgradeResponse
            }.Write(writer);
            new UpgradeResponse()
            {
                HostEndpoints = new[]
                {
                    HostEndpointMetadata.FromIP(networkTransport.Handler.GetLocalIp())
                },
                Endpoints = new[]
                {
                    TransportEndpoint.Tcp
                }
            }.Write(writer);
        });
    }

    void HandleUpgradeFinalization(CdpSocket socket, EndianReader reader)
    {
        var msg = TransportEndpoint.ParseArray(reader);
        _session.Platform.Handler.Log(0, $"Transport upgrade to {string.Join(',', msg.Select((x) => x.Type.ToString()))}");

        CommonHeader header = new()
        {
            Type = MessageType.Connect
        };

        _session.SendMessage(socket, header, (writer) =>
        {
            new ConnectionHeader()
            {
                ConnectionMode = ConnectionMode.Proximal,
                MessageType = ConnectionType.UpgradeFinalizationResponse
            }.Write(writer);
        });
    }

    void HandleUpgradeFailure(EndianReader reader)
    {
        var msg = HResultPayload.Parse(reader);
        _session.Platform.Handler.Log(0, $"Transport upgrade failed with HResult {msg.HResult}");
    }
}
